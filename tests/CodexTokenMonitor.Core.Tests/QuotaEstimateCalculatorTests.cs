using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class QuotaEstimateCalculatorTests : IDisposable
{
    private static readonly TimeSpan Beijing = TimeSpan.FromHours(8);
    private static readonly DateTimeOffset WindowStart = new(2026, 9, 10, 8, 0, 0, Beijing);
    private static readonly DateTimeOffset WindowEnd = WindowStart.AddHours(3);

    private readonly IDisposable cacheScope;
    private readonly string root;

    public QuotaEstimateCalculatorTests()
    {
        root = Path.Combine(Path.GetTempPath(), $"QuotaEstimateTests-{Guid.NewGuid():N}");
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

    private static CodexQuotaEstimate Estimate(decimal weekUsedPercent)
    {
        var week = new CodexQuotaWindowEstimate(
            "1周", weekUsedPercent, 10080, WindowStart, WindowEnd,
            WindowStart.AddDays(7), new TokenUsageSummary(), 0m, null, null);
        return new CodexQuotaEstimate(WindowEnd, "codex", LimitName: null, FiveHour: null, Week: week);
    }

    private static void SeedQuotaDay(DateOnly day, params (DateTimeOffset At, decimal Used)[] rows)
    {
        var snapshots = rows.Select(item => new CodexQuotaSnapshot(
            item.At, "codex", LimitName: null, FiveHourUsedPercent: null, FiveHourResetAtLocal: null,
            WeekUsedPercent: item.Used, WeekResetAtLocal: WindowStart.AddDays(7))).ToList();
        QuotaSnapshotCacheStore.Load("CodexTokenMonitor").Put(
            day, snapshots, isComplete: true, scannedThroughLocal: day.ToDateTime(TimeOnly.MaxValue) - TimeSpan.FromTicks(1),
            propagateErrors: true);
    }

    private void SeedUsage(DateTimeOffset at)
    {
        var day = DateOnly.FromDateTime(at.DateTime);
        var usageEvent = new TokenUsageEvent(
            at, InputTokens: 1_000_000, CachedInputTokens: 0, OutputTokens: 0,
            ReasoningOutputTokens: 0, TotalTokens: 1_000_000, Key: "estimate-event",
            ModelId: "gpt-5.6-sol");
        var bucket = new TokenUsageBucket
        {
            StartLocal = new DateTimeOffset(day.Year, day.Month, day.Day, 0, 0, 0, Beijing)
        };
        bucket.Add(usageEvent);
        UsageCacheStore.Load("CodexTokenMonitor").Put(
            bucket, isComplete: true, scannedThroughLocal: day.ToDateTime(TimeOnly.MaxValue) - TimeSpan.FromTicks(1),
            detailEvents: new[] { usageEvent }, propagateErrors: true);
    }

    [Fact]
    public void ManualWeekEstimate_WithoutWeekWindow_ExplainsMissing()
    {
        var estimate = new CodexQuotaEstimate(WindowEnd, "codex", null, null, null);

        var text = QuotaEstimateCalculator.BuildManualWeekEstimate(estimate, 50m, 20m);

        Assert.Equal("没有当前 7d 额度窗口", text);
    }

    [Fact]
    public void ManualWeekEstimate_SameRemaining_AsksForDifferentValues()
    {
        var text = QuotaEstimateCalculator.BuildManualWeekEstimate(Estimate(80m), 50m, 50m);

        Assert.Equal("请选择不同的剩余百分比", text);
    }

    [Fact]
    public void ManualWeekEstimate_TooFewSnapshots_ExplainsMissing()
    {
        var text = QuotaEstimateCalculator.BuildManualWeekEstimate(Estimate(80m), 50m, 20m);

        Assert.Equal("当前 7d 快照太少，暂时不能按区间估算", text);
    }

    [Fact]
    public void ManualWeekEstimate_StartThresholdNotReached_ExplainsProgress()
    {
        SeedQuotaDay(DateOnly.FromDateTime(WindowStart.DateTime),
            (WindowStart.AddHours(1), 10m), (WindowStart.AddHours(2), 20m));

        var text = QuotaEstimateCalculator.BuildManualWeekEstimate(Estimate(30m), 50m, 20m);

        Assert.Contains("还没进入剩余 50%", text);
    }

    [Fact]
    public void ManualWeekEstimate_EndThresholdNotReached_ExplainsProgress()
    {
        SeedQuotaDay(DateOnly.FromDateTime(WindowStart.DateTime),
            (WindowStart.AddHours(1), 55m), (WindowStart.AddHours(2), 60m));

        var text = QuotaEstimateCalculator.BuildManualWeekEstimate(Estimate(50m), 50m, 20m);

        Assert.Contains("还没到剩余 20%", text);
    }

    [Fact]
    public void ManualWeekEstimate_WithPricedUsage_ProducesLimitExtrapolation()
    {
        SeedQuotaDay(DateOnly.FromDateTime(WindowStart.DateTime),
            (WindowStart.AddHours(1), 55m), (WindowStart.AddHours(2), 85m));
        SeedUsage(WindowStart.AddMinutes(90));

        var text = QuotaEstimateCalculator.BuildManualWeekEstimate(Estimate(80m), 50m, 20m);

        // Consumed from 55% to 85% between the two anchors; the priced usage in
        // between extrapolates to a per-100% subscription value.
        Assert.Contains("45%->15% (30%)", text);
        Assert.Contains("当前组合 100%≈", text);
    }

    [Fact]
    public void BuildLoadResult_KeepsKnownPeriodsAndReportsTheirCount()
    {
        // HasCurrentPeriod only keeps caller-provided periods when one claims
        // the current window identity; otherwise the store is re-queried.
        var periods = new[]
        {
            new CodexQuotaCycle(WindowStart, WindowStart.AddDays(7), WindowStart.AddDays(7), 3, 21m, IsCurrent: true)
        };

        var result = QuotaEstimateCalculator.BuildLoadResult(Estimate(80m), WindowEnd, periods);

        Assert.Equal(1, result.PeriodCount);
        var period = Assert.Single(result.Periods);
        Assert.Equal(WindowStart, period.PeriodStart);
        Assert.True(period.IsCurrent);
    }
}
