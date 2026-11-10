using Xunit;

namespace CodexTokenMonitor.Core.Tests;

public sealed class CodexUsageRhythmTests
{
    private static readonly TimeSpan Offset = TimeSpan.FromHours(8);

    [Fact]
    public void SplitsMidnightInteractionAndKeepsTheFullSilenceOnItsStartDay()
    {
        DateTimeOffset[] messages = [At(1, 23, 50), At(2, 0, 20), At(2, 23, 50), At(3, 0, 10), At(4, 6)];
        var history = new CodexMessageHistory(messages, [], At(4, 9), "test", 0);
        var rhythm = CodexUsageRhythm.Calculate(history, Offset);

        Assert.Equal(2, history.CountInRange(At(2, 0), At(3, 0)));
        Assert.Collection(rhythm.Days,
            day =>
            {
                Assert.Equal(2, day.Messages);
                Assert.Equal(2, day.ClockHours);
                Assert.Equal(0.5, day.InteractionHours);
                Assert.Equal(23.5, day.LongestSilence?.Hours);
            },
            day =>
            {
                Assert.Equal(1, day.Messages);
                Assert.Equal(1d / 6, day.InteractionHours);
                Assert.Equal(At(4, 6), day.LongestSilence?.End);
            });
        Assert.Equal(At(3, 0, 10), Assert.Single(rhythm.LongSilences).Start);
        var week = Assert.Single(rhythm.Weeks);
        Assert.Equal(2, week.WindowCount);
        Assert.Equal(TimeSpan.FromMinutes(10), week.WindowStart);
        Assert.Equal(week.WindowStart.Add(TimeSpan.FromMinutes(10)), week.WindowEnd);
    }

    [Fact]
    public void SkipsGapsCrossingUnobservedDaysAndKeepsConfirmedZeroDays()
    {
        var history = new CodexMessageHistory(
            [At(1, 10), At(2, 10), At(4, 10), At(4, 14), At(6, 10)],
            [At(5, 10)], At(6, 15), "test", 0);
        var rhythm = CodexUsageRhythm.Calculate(history, Offset);

        Assert.Null(rhythm.Days[0].LongestSilence);
        Assert.Null(rhythm.Days[1].Messages);
        Assert.Equal(0, rhythm.Days[3].Messages);
        Assert.Equal(0, rhythm.Days[3].InteractionHours);
        Assert.Equal(3, Assert.Single(rhythm.Months).Days);
        Assert.Equal(44, Assert.Single(rhythm.LongSilences).Hours);
    }

    [Fact]
    public void WeeklyWindowWrapsAroundMidnight()
    {
        var history = new CodexMessageHistory(
            [At(1, 12), At(2, 23, 50), At(3, 0, 10), At(4, 12)], [], At(4, 15), "test", 0);
        var week = Assert.Single(CodexUsageRhythm.Calculate(history, Offset).Weeks);
        Assert.Equal(2, week.WindowCount);
        Assert.Equal(TimeSpan.FromHours(23) + TimeSpan.FromMinutes(50), week.WindowStart);
        Assert.Equal(TimeSpan.FromMinutes(10), week.WindowEnd);
    }

    private static DateTimeOffset At(int day, int hour, int minute = 0) => new(2026, 11, day, hour, minute, 0, Offset);
}
