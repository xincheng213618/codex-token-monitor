using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class UsageRangePolicyTests
{
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
}
