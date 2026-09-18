using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class QuotaAnalysisRefreshRangeTests
{
    private static readonly TimeSpan Beijing = TimeSpan.FromHours(8);
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 16, 0, 0, Beijing);

    private static CodexQuotaCycle Cycle(
        DateTimeOffset periodStart,
        DateTimeOffset resetAt,
        bool isCurrent)
    {
        return new CodexQuotaCycle(periodStart, resetAt, resetAt, 12, 42m, isCurrent);
    }

    private static CodexQuotaSnapshot Snapshot(DateTimeOffset at, DateTimeOffset weekReset)
    {
        return new CodexQuotaSnapshot(
            at, "codex", LimitName: null, FiveHourUsedPercent: null, FiveHourResetAtLocal: null,
            WeekUsedPercent: 50m, WeekResetAtLocal: weekReset);
    }

    [Fact]
    public void ResolveCurrentAnalysisPeriod_HistoricalOriginal_ReturnsUnchanged()
    {
        var original = Cycle(Now.AddDays(-14), Now.AddDays(-7), isCurrent: false);

        var result = QuotaAnalysisRefreshRange.ResolveCurrentAnalysisPeriod(original, null, Now, Array.Empty<CodexQuotaSnapshot>());

        Assert.False(result.CycleEnded);
        Assert.Equal("", result.Reason);
        Assert.Equal(original.PeriodStart, result.Period.PeriodStart);
        Assert.Equal(original.PeriodEnd, result.Period.PeriodEnd);
    }

    [Fact]
    public void ResolveCurrentAnalysisPeriod_CurrentPeriod_ClampsEndToNow()
    {
        var original = Cycle(Now.AddDays(-2), Now.AddDays(5), isCurrent: true);
        var snapshots = new[] { Snapshot(Now.AddMinutes(-60), original.ResetAt) };

        var result = QuotaAnalysisRefreshRange.ResolveCurrentAnalysisPeriod(original, null, Now, snapshots);

        Assert.False(result.CycleEnded);
        Assert.Equal("", result.Reason);
        Assert.True(result.Period.IsCurrent);
        Assert.Equal(Now, result.Period.PeriodEnd);
    }

    [Fact]
    public void ResolveCurrentAnalysisPeriod_NoTrustedSnapshots_FreezesAtNow()
    {
        var original = Cycle(Now.AddDays(-2), Now.AddDays(5), isCurrent: true);

        var result = QuotaAnalysisRefreshRange.ResolveCurrentAnalysisPeriod(original, null, Now, Array.Empty<CodexQuotaSnapshot>());

        Assert.False(result.CycleEnded);
        Assert.Contains("没有可信", result.Reason);
        Assert.Equal(Now, result.Period.PeriodEnd);
    }

    [Fact]
    public void ResolveCurrentAnalysisPeriod_ExpiredCurrentPage_MarksCycleEnded()
    {
        var original = Cycle(Now.AddDays(-9), Now.AddHours(-1), isCurrent: true);

        var result = QuotaAnalysisRefreshRange.ResolveCurrentAnalysisPeriod(original, null, Now, Array.Empty<CodexQuotaSnapshot>());

        Assert.True(result.CycleEnded);
        Assert.Contains("已到期", result.Reason);
        Assert.False(result.Period.IsCurrent);
    }

    [Fact]
    public void ResolveCurrentAnalysisPeriod_ResetIdentityChangedWithoutBoundary_EndsPage()
    {
        // The page's reset is already past, but a newer snapshot reports a
        // different identity whose boundary the snapshots cannot establish.
        var original = Cycle(Now.AddDays(-9), Now.AddHours(-1), isCurrent: true);
        var snapshots = new[] { Snapshot(Now.AddMinutes(-30), Now.AddHours(2)) };

        var result = QuotaAnalysisRefreshRange.ResolveCurrentAnalysisPeriod(original, null, Now, snapshots);

        Assert.True(result.CycleEnded);
        Assert.Contains("额度重置标识已变化", result.Reason);
        Assert.Equal(original.ResetAt, result.Period.PeriodEnd);
    }
}
