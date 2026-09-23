using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class QuotaCycleAnalysisQueryServiceTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 1, 0, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public void PartialAnalysisWithCacheFailureNeverReachesCalibration()
    {
        var source = new FakeSource { DuringAnalysis = () => ReportFailure() };
        var service = new QuotaCycleAnalysisQueryService(source);
        using var diagnostics = CacheOperationDiagnostics.Begin();

        var result = service.Execute(new(Period()));

        Assert.True(result.Analysis.HasData); // Partial data still looks plausible.
        Assert.Equal(0, source.CalibrationCalls);
        Assert.Empty(result.Capacities.Estimates);
        Assert.Single(diagnostics.Warnings);
    }

    [Fact]
    public void FailedCurrentSnapshotReadDoesNotStartLogScanOrCalibration()
    {
        var source = new FakeSource { DuringSnapshots = () => ReportFailure() };
        var service = new QuotaCycleAnalysisQueryService(source, new FixedClock(Start.AddDays(2)));
        using var diagnostics = CacheOperationDiagnostics.Begin();

        var result = service.Execute(new(Period() with { IsCurrent = true }));

        Assert.False(result.Analysis.HasData);
        Assert.Equal(0, source.AnalysisCalls);
        Assert.Equal(0, source.CalibrationCalls);
        Assert.Single(diagnostics.Warnings);
    }

    [Fact]
    public void RecoveryUsesFreshDiagnosticsAndPreservesPreviousPeriodContext()
    {
        var source = new FakeSource { DuringAnalysis = () => ReportFailure() };
        var service = new QuotaCycleAnalysisQueryService(source);
        var previous = Period() with { PeriodStart = Start.AddDays(-7), PeriodEnd = Start, ResetAt = Start };
        using (var failed = CacheOperationDiagnostics.Begin())
        {
            service.Execute(new(Period(), PreviousPeriod: previous));
            Assert.Single(failed.Warnings);
        }
        source.DuringAnalysis = null;
        using var recovered = CacheOperationDiagnostics.Begin();

        var result = service.Execute(new(Period(), PreviousPeriod: previous));

        Assert.Empty(recovered.Warnings);
        Assert.Equal(1, source.CalibrationCalls);
        Assert.Same(previous, source.PreviousPeriod);
        Assert.Same(source.Capacities, result.Capacities);
    }

    [Fact]
    public void HistoricalAnalysisKeepsSelectedRangeAndSkipsCurrentSnapshotLookup()
    {
        var source = new FakeSource();
        var period = Period();
        var result = new QuotaCycleAnalysisQueryService(source).Execute(new(period));

        Assert.Equal(0, source.SnapshotCalls);
        Assert.Equal(period, source.AnalysedPeriod);
        Assert.Equal(period, result.Analysis.Period);
        Assert.Equal(1, source.CalibrationCalls);
    }

    [Fact]
    public void SelectedBandSizeReachesAnalysisWhileCalibrationUsesFivePercentBands()
    {
        var source = new FakeSource();
        var result = new QuotaCycleAnalysisQueryService(source).Execute(
            new QuotaCycleAnalysisRequest(Period(), BandSizePercent: 1m));

        Assert.Equal(1m, source.AnalysedBandSizePercent);
        Assert.Equal(1m, result.Analysis.BandSizePercent);
        Assert.Equal(5m, source.CalibratedBandSizePercent);
    }

    [Fact]
    public void CurrentAnalysisWithoutTrustedSnapshotsDoesNotExtendOriginalRange()
    {
        var source = new FakeSource();
        var period = Period() with { IsCurrent = true };
        var now = Start.AddDays(2);
        var service = new QuotaCycleAnalysisQueryService(source, new FixedClock(now));

        var result = service.Execute(new(period));

        Assert.Equal(period.PeriodEnd, source.AnalysedPeriod!.PeriodEnd);
        Assert.Equal(Start.AddMinutes(-10), source.SnapshotStart);
        Assert.Equal(now.AddTicks(1), source.SnapshotEnd);
        Assert.Contains("保留原分析范围", result.RefreshReason);
    }

    [Fact]
    public void CancellationAfterReadingNeverWritesCalibration()
    {
        using var cancelled = new CancellationTokenSource();
        var source = new FakeSource { DuringAnalysis = cancelled.Cancel };
        using var diagnostics = CacheOperationDiagnostics.Begin();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            new QuotaCycleAnalysisQueryService(source).Execute(new(Period()), cancelled.Token));

        Assert.Equal(0, source.CalibrationCalls);
        Assert.Empty(diagnostics.Warnings);
    }

    [Fact]
    public void CalibrationWarningIsReturnedToTheQuerySession()
    {
        var source = new FakeSource { DuringCalibration = () => ReportFailure() };
        using var diagnostics = CacheOperationDiagnostics.Begin();

        new QuotaCycleAnalysisQueryService(source).Execute(new(Period()));

        Assert.Equal(1, source.CalibrationCalls);
        Assert.Single(diagnostics.Warnings);
    }

    private static void ReportFailure() =>
        CacheOperationDiagnostics.Report("isolated-cache", "test read", new IOException("cache unavailable"));

    private static CodexQuotaCycle Period() => new(Start, Start.AddDays(1), Start.AddDays(7), 3, 10m, false);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    }

    private sealed class FakeSource : IQuotaCycleAnalysisSource
    {
        public Action? DuringSnapshots { get; init; }
        public Action? DuringAnalysis { get; set; }
        public Action? DuringCalibration { get; init; }
        public int SnapshotCalls { get; private set; }
        public int AnalysisCalls { get; private set; }
        public int CalibrationCalls { get; private set; }
        public CodexQuotaCycle? AnalysedPeriod { get; private set; }
        public decimal AnalysedBandSizePercent { get; private set; }
        public decimal CalibratedBandSizePercent { get; private set; }
        public CodexQuotaCycle? PreviousPeriod { get; private set; }
        public DateTimeOffset SnapshotStart { get; private set; }
        public DateTimeOffset SnapshotEnd { get; private set; }
        public QuotaModelCapacityReport Capacities { get; } = new("test plan", Array.Empty<QuotaModelCapacityEstimate>());

        public IReadOnlyList<CodexQuotaSnapshot> ReadSnapshots(DateTimeOffset start, DateTimeOffset end, CancellationToken token)
        {
            SnapshotCalls++;
            SnapshotStart = start;
            SnapshotEnd = end;
            DuringSnapshots?.Invoke();
            return Array.Empty<CodexQuotaSnapshot>();
        }

        public QuotaCycleAnalysisResult BuildAnalysis(CodexQuotaCycle period, CodexQuotaWindowEstimate? currentWeek,
            decimal bandSizePercent, CancellationToken token)
        {
            AnalysisCalls++;
            AnalysedPeriod = period;
            AnalysedBandSizePercent = bandSizePercent;
            DuringAnalysis?.Invoke();
            var calibration = QuotaCycleAnalysisResult.Empty(period, "") with
            {
                Bands = new[] { new QuotaCycleAnalysisBand(0, 0m, 5m, Start, Start.AddHours(1),
                    5m, 100, 1m, 20m, "test model", Array.Empty<QuotaCycleModelShare>()) }
            };
            return calibration with
            {
                BandSizePercent = bandSizePercent,
                CalibrationAnalysis = bandSizePercent == 5m ? null : calibration
            };
        }

        public QuotaModelCapacityReport BuildCapacities(CodexQuotaCycle period, QuotaCycleAnalysisResult analysis,
            CodexQuotaCycle? previousPeriod, CancellationToken token)
        {
            CalibrationCalls++;
            PreviousPeriod = previousPeriod;
            CalibratedBandSizePercent = analysis.BandSizePercent;
            DuringCalibration?.Invoke();
            return Capacities;
        }
    }
}
