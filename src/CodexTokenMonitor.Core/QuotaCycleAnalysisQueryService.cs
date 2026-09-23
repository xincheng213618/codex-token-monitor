namespace CodexTokenMonitor;

internal sealed record QuotaCycleAnalysisRequest(
    CodexQuotaCycle Period,
    CodexQuotaWindowEstimate? CurrentWeek = null,
    CodexQuotaCycle? PreviousPeriod = null,
    decimal BandSizePercent = QuotaCycleAnalysisCalculator.DefaultBandSizePercent);

internal sealed record QuotaCycleAnalysisLoadResult(
    QuotaCycleAnalysisResult Analysis,
    QuotaModelCapacityReport Capacities,
    string RefreshReason);

internal interface IQuotaCycleAnalysisSource
{
    IReadOnlyList<CodexQuotaSnapshot> ReadSnapshots(DateTimeOffset start, DateTimeOffset end, CancellationToken token);
    QuotaCycleAnalysisResult BuildAnalysis(CodexQuotaCycle period, CodexQuotaWindowEstimate? currentWeek,
        decimal bandSizePercent, CancellationToken token);
    QuotaModelCapacityReport BuildCapacities(CodexQuotaCycle period, QuotaCycleAnalysisResult analysis,
        CodexQuotaCycle? previousPeriod, CancellationToken token);
}

/// <summary>
/// Loads a selected cycle and its calibration as one operation. The caller must
/// hold the shared I/O gate: current cycles scan logs and all cycles may persist
/// calibration. Cache failures must never feed incomplete samples into calibration.
/// </summary>
internal sealed class QuotaCycleAnalysisQueryService(
    IQuotaCycleAnalysisSource? source = null,
    TimeProvider? timeProvider = null)
{
    private readonly IQuotaCycleAnalysisSource source = source ?? new DefaultSource();
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    // A provisional view must neither scan source logs nor train/persist model
    // calibrations using potentially incomplete days. It can bypass the I/O gate.
    public QuotaCycleAnalysisLoadResult ExecuteCached(QuotaCycleAnalysisRequest request, CancellationToken token = default) =>
        new QuotaCycleAnalysisQueryService(new CachedSource(), clock).Execute(request, token);

    public QuotaCycleAnalysisLoadResult Execute(QuotaCycleAnalysisRequest request, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        token.ThrowIfCancellationRequested();
        using var diagnostics = CacheOperationDiagnostics.Begin(propagateToParent: true);
        var (period, currentWeek, previousPeriod, bandSizePercent) = request;
        var range = new QuotaAnalysisRefreshRange(period, currentWeek, false, "");
        if (period.IsCurrent)
        {
            var now = clock.GetUtcNow().ToOffset(CodexUsageReader.BeijingOffset);
            var snapshotStart = currentWeek is { ResetAtLocal: not null } &&
                                CodexQuotaCycleReader.IsSameQuotaReset(currentWeek.ResetAtLocal, period.ResetAt) &&
                                currentWeek.WindowStartLocal < period.PeriodStart
                ? currentWeek.WindowStartLocal : period.PeriodStart;
            var snapshots = source.ReadSnapshots(snapshotStart.AddMinutes(-10), now.AddTicks(1), token);
            token.ThrowIfCancellationRequested();
            if (diagnostics.Warnings.Count > 0)
                return Unavailable(period);
            range = QuotaAnalysisRefreshRange.ResolveCurrentAnalysisPeriod(period, currentWeek, now, snapshots);
        }

        var analysis = source.BuildAnalysis(range.Period, range.CurrentWeek, bandSizePercent, token);
        token.ThrowIfCancellationRequested();
        // A reader may preserve its old fallback contract and return partial
        // samples. Stop before any calibration writes, even when HasData is true.
        var calibrationAnalysis = analysis.CalibrationAnalysis ?? analysis;
        var capacities = analysis.HasData && calibrationAnalysis.HasData && diagnostics.Warnings.Count == 0
            ? source.BuildCapacities(range.Period, calibrationAnalysis, previousPeriod, token)
            : EmptyCapacities();
        token.ThrowIfCancellationRequested();
        return new(analysis, capacities, range.Reason);
    }

    private static QuotaCycleAnalysisLoadResult Unavailable(CodexQuotaCycle period) =>
        new(QuotaCycleAnalysisResult.Empty(period, "额度缓存暂不可用"), EmptyCapacities(), "");

    private static QuotaModelCapacityReport EmptyCapacities() => new("-", Array.Empty<QuotaModelCapacityEstimate>());

    private sealed class CachedSource : IQuotaCycleAnalysisSource
    {
        public IReadOnlyList<CodexQuotaSnapshot> ReadSnapshots(DateTimeOffset start, DateTimeOffset end, CancellationToken token) =>
            UsageSourceReaders.Codex.ReadCachedAndHistoricalQuotaSnapshots(start, end, token);

        public QuotaCycleAnalysisResult BuildAnalysis(CodexQuotaCycle period, CodexQuotaWindowEstimate? currentWeek,
            decimal bandSizePercent, CancellationToken token) =>
            QuotaCycleAnalysisCalculator.BuildCached(period, currentWeek, bandSizePercent, token);

        public QuotaModelCapacityReport BuildCapacities(CodexQuotaCycle period, QuotaCycleAnalysisResult analysis,
            CodexQuotaCycle? previousPeriod, CancellationToken token) => EmptyCapacities();
    }

    private sealed class DefaultSource : IQuotaCycleAnalysisSource
    {
        public IReadOnlyList<CodexQuotaSnapshot> ReadSnapshots(DateTimeOffset start, DateTimeOffset end, CancellationToken token) =>
            UsageSourceReaders.Codex.ReadCachedAndHistoricalQuotaSnapshots(start, end, token);

        public QuotaCycleAnalysisResult BuildAnalysis(CodexQuotaCycle period, CodexQuotaWindowEstimate? currentWeek,
            decimal bandSizePercent, CancellationToken token) =>
            QuotaCycleAnalysisCalculator.Build(period, currentWeek, bandSizePercent, token);

        public QuotaModelCapacityReport BuildCapacities(CodexQuotaCycle period, QuotaCycleAnalysisResult analysis,
            CodexQuotaCycle? previousPeriod, CancellationToken token) =>
            QuotaModelCapacityCalibrationService.Build(period, analysis, previousPeriod, token);
    }
}
