using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class UsageRangePolicyTests
{
    [Fact]
    public void ResolveSelectedRange_CurrentDay_ClampsEndToNow()
    {
        var now = new DateTimeOffset(2026, 9, 19, 16, 30, 0, Beijing);
        var picker = new DateTimeOffset(2026, 9, 19, 10, 0, 0, Beijing);

        var range = UsageRangePolicy.ResolveSelectedRange(RangeMode.Day, picker, null, null, now);

        Assert.Equal(new DateTimeOffset(2026, 9, 19, 0, 0, 0, Beijing), range.Start);
        Assert.Equal(now, range.End);
        Assert.Equal("今天", range.Title);
        Assert.True(range.FollowsCurrent);
    }

    [Fact]
    public void ResolveSelectedRange_HistoricalDay_EndsAtMidnightNextDay()
    {
        var now = new DateTimeOffset(2026, 9, 19, 16, 30, 0, Beijing);
        var picker = new DateTimeOffset(2026, 9, 12, 8, 0, 0, Beijing);

        var range = UsageRangePolicy.ResolveSelectedRange(RangeMode.Day, picker, null, null, now);

        Assert.Equal(new DateTimeOffset(2026, 9, 12, 0, 0, 0, Beijing), range.Start);
        Assert.Equal(new DateTimeOffset(2026, 9, 13, 0, 0, 0, Beijing), range.End);
        Assert.Equal("2026-09-12", range.Title);
        Assert.False(range.FollowsCurrent);
    }

    [Fact]
    public void ResolveSelectedRange_CurrentWeek_ClampsEndToNow()
    {
        var now = new DateTimeOffset(2026, 9, 19, 16, 30, 0, Beijing);
        var picker = now;

        var range = UsageRangePolicy.ResolveSelectedRange(RangeMode.Week, picker, null, null, now);

        Assert.Equal(now.AddDays(-7), range.Start);
        Assert.Equal(now, range.End);
        Assert.Equal("近一周", range.Title);
        Assert.True(range.FollowsCurrent);
    }

    [Fact]
    public void ResolveSelectedRange_WeekPickerEarlierToday_EndsAtPicker()
    {
        var now = new DateTimeOffset(2026, 9, 19, 16, 30, 0, Beijing);
        var picker = new DateTimeOffset(2026, 9, 19, 12, 0, 0, Beijing);

        var range = UsageRangePolicy.ResolveSelectedRange(RangeMode.Week, picker, null, null, now);

        Assert.Equal(picker.AddDays(-7), range.Start);
        Assert.Equal(picker, range.End);
        Assert.Contains("7天至", range.Title);
        Assert.False(range.FollowsCurrent);
    }

    [Fact]
    public void ResolveSelectedRange_HistoricalWeek_EndsAtPickerAndStopsFollowing()
    {
        var now = new DateTimeOffset(2026, 9, 19, 16, 30, 0, Beijing);
        var picker = new DateTimeOffset(2026, 9, 10, 9, 0, 0, Beijing);

        var range = UsageRangePolicy.ResolveSelectedRange(RangeMode.Week, picker, null, null, now);

        Assert.Equal(picker.AddDays(-7), range.Start);
        Assert.Equal(picker, range.End);
        Assert.Contains("7天至", range.Title);
        Assert.False(range.FollowsCurrent);
    }

    [Fact]
    public void ResolveSelectedRange_CurrentMonth_StartsAtMonthBegin()
    {
        var now = new DateTimeOffset(2026, 9, 19, 16, 30, 0, Beijing);
        var picker = new DateTimeOffset(2026, 9, 5, 0, 0, 0, Beijing);

        var range = UsageRangePolicy.ResolveSelectedRange(RangeMode.Month, picker, null, null, now);

        Assert.Equal(new DateTimeOffset(2026, 9, 1, 0, 0, 0, Beijing), range.Start);
        Assert.Equal(now, range.End);
        Assert.Equal("本月", range.Title);
        Assert.True(range.FollowsCurrent);
    }

    [Fact]
    public void ResolveSelectedRange_HistoricalMonth_EndsAtMonthEnd()
    {
        var now = new DateTimeOffset(2026, 9, 19, 16, 30, 0, Beijing);
        var picker = new DateTimeOffset(2026, 8, 5, 0, 0, 0, Beijing);

        var range = UsageRangePolicy.ResolveSelectedRange(RangeMode.Month, picker, null, null, now);

        Assert.Equal(new DateTimeOffset(2026, 8, 1, 0, 0, 0, Beijing), range.Start);
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 0, 0, 0, Beijing), range.End);
        Assert.Equal("2026-08", range.Title);
        Assert.False(range.FollowsCurrent);
    }

    [Fact]
    public void ResolveSelectedRange_CustomStart_WinsOverMode()
    {
        var now = new DateTimeOffset(2026, 9, 19, 16, 30, 0, Beijing);
        var picker = new DateTimeOffset(2026, 9, 5, 0, 0, 0, Beijing);
        var start = new DateTimeOffset(2026, 9, 19, 14, 0, 0, Beijing);

        var range = UsageRangePolicy.ResolveSelectedRange(RangeMode.Month, picker, start, null, now);

        Assert.Equal(RangeMode.Day, range.Mode);
        Assert.Equal(start, range.Start);
        Assert.Equal(now, range.End);
        Assert.Contains("当前起算", range.Title);
        Assert.True(range.IsCustomStart);
        Assert.False(range.FollowsCurrent);
    }

    [Fact]
    public void ResolveSelectedRange_FutureCustomStart_ClampsEndToStart()
    {
        var now = new DateTimeOffset(2026, 9, 19, 16, 30, 0, Beijing);
        var start = new DateTimeOffset(2026, 9, 19, 18, 0, 0, Beijing);

        var range = UsageRangePolicy.ResolveSelectedRange(RangeMode.Day, now, start, null, now);

        Assert.Equal(start, range.Start);
        Assert.Equal(start, range.End);
    }

    [Fact]
    public void ResolveSelectedRange_MissingCycle_ReturnsEmptyCycleRange()
    {
        var now = new DateTimeOffset(2026, 9, 19, 16, 30, 0, Beijing);

        var range = UsageRangePolicy.ResolveSelectedRange(RangeMode.Cycle, now, null, null, now);

        Assert.Equal(RangeMode.Cycle, range.Mode);
        Assert.Equal(now, range.Start);
        Assert.Equal(now, range.End);
    }

    [Fact]
    public void ResolveSelectedRange_CurrentCycle_ClampsEndToNow()
    {
        var now = new DateTimeOffset(2026, 9, 19, 16, 30, 0, Beijing);
        var cycle = new CodexQuotaCycle(
            now.AddDays(-3), now.AddDays(4), now.AddDays(4), 12, 42m, IsCurrent: true);

        var range = UsageRangePolicy.ResolveSelectedRange(RangeMode.Cycle, now, null, cycle, now);

        Assert.Equal(cycle.PeriodStart, range.Start);
        Assert.Equal(now, range.End);
        Assert.Equal("当前周期", range.Title);
        Assert.True(range.FollowsCurrent);
    }

    [Fact]
    public void ResolveSelectedRange_HistoricalCycle_EndsAtPeriodEnd()
    {
        var now = new DateTimeOffset(2026, 9, 19, 16, 30, 0, Beijing);
        var cycle = new CodexQuotaCycle(
            now.AddDays(-14), now.AddDays(-7), now.AddDays(-7), 30, 87.5m, IsCurrent: false);

        var range = UsageRangePolicy.ResolveSelectedRange(RangeMode.Cycle, now, null, cycle, now);

        Assert.Equal(cycle.PeriodStart, range.Start);
        Assert.Equal(cycle.PeriodEnd, range.End);
        Assert.Contains("周期", range.Title);
        Assert.False(range.FollowsCurrent);
    }

    [Fact]
    public void ResolveSelectedRange_InvertedCycle_ClampsEndToStart()
    {
        var now = new DateTimeOffset(2026, 9, 19, 16, 30, 0, Beijing);
        var cycle = new CodexQuotaCycle(
            now.AddDays(-7), now.AddDays(-14), now.AddDays(-14), 2, 3m, IsCurrent: false);

        var range = UsageRangePolicy.ResolveSelectedRange(RangeMode.Cycle, now, null, cycle, now);

        Assert.Equal(cycle.PeriodStart, range.Start);
        Assert.Equal(cycle.PeriodStart, range.End);
    }

    private static readonly TimeSpan Beijing = TimeSpan.FromHours(8);

    [Fact]
    public void ShouldReadLiveToday_IncludesCurrentDayWeekAndMonthRanges()
    {
        var now = new DateTimeOffset(2026, 8, 20, 16, 30, 0, Beijing);
        var todayStart = new DateTimeOffset(2026, 8, 20, 0, 0, 0, Beijing);

        Assert.True(UsageRangePolicy.ShouldReadLiveToday(
            new SelectedRange(todayStart, now, "day", "", RangeMode.Day),
            now.ToUniversalTime()));
        Assert.True(UsageRangePolicy.ShouldReadLiveToday(
            new SelectedRange(now.AddDays(-7), now, "week", "", RangeMode.Week),
            now.ToUniversalTime()));
        Assert.True(UsageRangePolicy.ShouldReadLiveToday(
            new SelectedRange(new DateTimeOffset(2026, 8, 1, 0, 0, 0, Beijing), now, "month", "", RangeMode.Month),
            now.ToUniversalTime()));
    }

    [Fact]
    public void ShouldReadLiveToday_RejectsHistoricalAndFutureRanges()
    {
        var now = new DateTimeOffset(2026, 8, 20, 16, 30, 0, Beijing);

        Assert.False(UsageRangePolicy.ShouldReadLiveToday(
            new SelectedRange(now.AddDays(-8), now.AddDays(-1), "past", "", RangeMode.Week),
            now));
        Assert.False(UsageRangePolicy.ShouldReadLiveToday(
            new SelectedRange(now.AddDays(1), now.AddDays(2), "future", "", RangeMode.Day),
            now));
        Assert.False(UsageRangePolicy.ShouldReadLiveToday(
            new SelectedRange(now.AddDays(-7), now.AddSeconds(-3), "stale", "", RangeMode.Week),
            now));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void AutomaticRefresh_AdvancesPreviouslyCurrentRangeAcrossMidnight(int modeValue)
    {
        var mode = (RangeMode)modeValue;
        var beforeMidnight = new DateTimeOffset(2026, 9, 30, 23, 59, 30, Beijing);
        var afterMidnight = new DateTimeOffset(2026, 10, 1, 0, 1, 0, Beijing);
        var previous = UsageRangePolicy.ResolveSelectedRange(mode, beforeMidnight, null, null, beforeMidnight);
        var selected = UsageRangePolicy.ResolveSelectedRange(mode, beforeMidnight, null, null, afterMidnight);

        Assert.True(previous.FollowsCurrent);
        Assert.False(selected.FollowsCurrent);
        Assert.Equal(AutomaticRefreshAction.AdvanceCurrentPeriod,
            UsageRangePolicy.GetAutomaticRefreshAction(mode, selected, previous, afterMidnight));
    }

    [Fact]
    public void AutomaticRefresh_DoesNotReplaceHistoricalSelection()
    {
        var now = new DateTimeOffset(2026, 10, 1, 0, 1, 0, Beijing);
        var yesterday = now.AddDays(-1);
        var selected = UsageRangePolicy.ResolveSelectedRange(RangeMode.Day, yesterday, null, null, now);
        var previous = UsageRangePolicy.ResolveSelectedRange(RangeMode.Day, yesterday, null, null, now.AddSeconds(-1));

        Assert.Equal(AutomaticRefreshAction.RefreshQuotaOnly,
            UsageRangePolicy.GetAutomaticRefreshAction(RangeMode.Day, selected, previous, now));
    }

    [Fact]
    public void AutomaticRefresh_KeepsCurrentMonthLiveWithinTheSameMonth()
    {
        var now = new DateTimeOffset(2026, 9, 20, 0, 1, 0, Beijing);
        var selected = UsageRangePolicy.ResolveSelectedRange(RangeMode.Month, now.AddDays(-1), null, null, now);

        Assert.Equal(AutomaticRefreshAction.RefreshLive,
            UsageRangePolicy.GetAutomaticRefreshAction(RangeMode.Month, selected, null, now));
    }
}
