using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class QuotaCostCurveCalculatorTests : IDisposable
{
    private static readonly TimeSpan Beijing = TimeSpan.FromHours(8);
    private static readonly DateTimeOffset PeriodStart = new(2026, 9, 10, 8, 0, 0, Beijing);
    private static readonly DateTimeOffset PeriodEnd = new(2026, 9, 17, 8, 0, 0, Beijing);

    private readonly IDisposable cacheScope;
    private readonly string root;

    public QuotaCostCurveCalculatorTests()
    {
        root = Path.Combine(Path.GetTempPath(), $"QuotaCurveTests-{Guid.NewGuid():N}");
        cacheScope = MonitorCachePaths.PushLocalAppDataRoot(root);
        UsageCacheStore.Delete("CodexTokenMonitor");
    }

    public void Dispose()
    {
        cacheScope.Dispose();
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch
        {
            // Best-effort cleanup of the temporary tree.
        }
    }

    private static CodexQuotaCycle Period() =>
        new(PeriodStart, PeriodEnd, PeriodEnd, 3, 21m, IsCurrent: false);

    private static TokenUsageEvent Event(DateTimeOffset at, long total) =>
        new(at, InputTokens: total, CachedInputTokens: 0, OutputTokens: 0,
            ReasoningOutputTokens: 0, TotalTokens: total, Key: $"curve-{at:yyyyMMddHHmm}");

    private void SeedUsageAndTimeline(params (DateTimeOffset At, long Total, decimal UsedPercent)[] rows)
    {
        var cache = UsageCacheStore.Load("CodexTokenMonitor");
        foreach (var group in rows.GroupBy(item => item.At.Date))
        {
            var day = group.Key;
            var events = group.Select(item => Event(item.At, item.Total)).ToList();
            var bucket = new TokenUsageBucket
            {
                StartLocal = new DateTimeOffset(day.Year, day.Month, day.Day, 0, 0, 0, Beijing)
            };
            foreach (var usageEvent in events)
            {
                bucket.Add(usageEvent);
            }

            cache.Put(
                bucket,
                isComplete: true,
                scannedThroughLocal: day.AddDays(1).AddTicks(-1),
                detailEvents: events,
                propagateErrors: true);
        }

        var quotaCache = QuotaSnapshotCacheStore.Load("CodexTokenMonitor");
        var snapshots = rows
            .Select(item => new CodexQuotaSnapshot(
                item.At, "codex", LimitName: null, FiveHourUsedPercent: null, FiveHourResetAtLocal: null,
                WeekUsedPercent: item.UsedPercent, WeekResetAtLocal: PeriodEnd))
            .ToList();
        quotaCache.PutTimelineSnapshots(
            snapshots,
            snapshots.ToDictionary(
                item => item.SnapshotLocal,
                _ => ((DateTimeOffset?)null, (DateTimeOffset?)null)),
            cancellationToken: default);
    }

    [Fact]
    public void Build_HistoricalPeriod_ProducesMonotonicCurvePoints()
    {
        SeedUsageAndTimeline(
            (PeriodStart.AddHours(2), 1_000, 10m),
            (PeriodStart.AddHours(20), 2_000, 30m),
            (PeriodStart.AddDays(2), 3_000, 60m));

        var result = QuotaCostCurveCalculator.Build(MinimalEstimate(), new[] { Period() });

        var curve = Assert.Single(result.Curves);
        Assert.False(curve.IsCurrent);
        Assert.Equal(3, curve.Points.Count);
        Assert.All(curve.Points, item => Assert.InRange(item.UsedPercent, 0, 100));
        Assert.True(curve.Points[0].UsedPercent < curve.Points[1].UsedPercent);
        Assert.True(curve.Points[1].UsedPercent < curve.Points[2].UsedPercent);
        Assert.Equal(PeriodStart.AddHours(2), curve.Points[0].TimestampLocal);
        Assert.Equal(PeriodStart.AddDays(2), curve.Points[2].TimestampLocal);
    }

    [Fact]
    public void Build_NoQuotaTimeline_ProducesNoCurves()
    {
        SeedUsageAndTimeline((PeriodStart.AddHours(2), 1_000, 10m));

        // Only usage rows: without quota anchors no point can be placed, so the
        // calculator must report an empty curve set instead of a partial curve.
        var cache = UsageCacheStore.Load("CodexTokenMonitor");
        Assert.NotEmpty(cache.GetDetailEvents(
            DateOnly.FromDateTime(PeriodStart.DateTime), cancellationToken: default));

        var result = QuotaCostCurveCalculator.Build(MinimalEstimate(), new[] { Period() });

        Assert.Empty(result.Curves);
    }

    [Fact]
    public void Build_Bands_CoverTenPercentRanges()
    {
        SeedUsageAndTimeline(
            (PeriodStart.AddHours(2), 1_000, 5m),
            (PeriodStart.AddHours(30), 2_000, 45m),
            (PeriodStart.AddDays(3), 3_000, 95m));

        var result = QuotaCostCurveCalculator.Build(MinimalEstimate(), new[] { Period() });

        Assert.Equal(10, result.Bands.Count);
        Assert.All(result.Bands, band => Assert.Contains("%", band.Range));
    }

    private static CodexQuotaEstimate MinimalEstimate()
    {
        return new CodexQuotaEstimate(PeriodEnd, "codex", LimitName: null, FiveHour: null, Week: null);
    }
}
