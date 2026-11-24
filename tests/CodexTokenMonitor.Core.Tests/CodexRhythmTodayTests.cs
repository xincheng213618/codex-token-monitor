using Xunit;

namespace CodexTokenMonitor.Core.Tests;

public sealed class CodexRhythmTodayTests
{
    [Theory]
    [InlineData(330)]
    [InlineData(-210)]
    public void CalculatesThePartialLocalDayWithAnExclusiveCutoff(int offsetMinutes)
    {
        var offset = TimeSpan.FromMinutes(offsetMinutes);
        var start = new DateTimeOffset(2026, 11, 22, 0, 0, 0, offset).ToUniversalTime();
        var cutoff = start.AddHours(2.5);
        var history = new CodexMessageHistory(
            [start.AddTicks(-1), start, start.AddMinutes(45), cutoff.AddTicks(-1), cutoff, cutoff.AddMinutes(5)],
            [], cutoff.AddMinutes(6), "test", 0);

        var today = CodexRhythmToday.Calculate(history, cutoff, offset, 12_345);

        Assert.Equal(cutoff, today.Cutoff);
        Assert.Equal(offset, today.Cutoff.Offset);
        Assert.Equal(new DateOnly(2026, 11, 22), today.Day.Date);
        Assert.Equal(3, today.Day.Messages);
        Assert.Equal(2, today.Day.ClockHours);
        Assert.Equal(12_345L, today.TotalTokens);
        Assert.Null(today.Day.InteractionHours);
        Assert.Null(today.Day.LongestSilence);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DistinguishesAnObservedZeroFromMissingMessages(bool hasTurnBeforeCutoff)
    {
        var offset = TimeSpan.FromHours(8);
        var start = new DateTimeOffset(2026, 11, 22, 0, 0, 0, offset);
        var cutoff = start.AddHours(1);
        var turn = hasTurnBeforeCutoff ? start : cutoff;
        var history = new CodexMessageHistory([start.AddTicks(-1)], [start.AddTicks(-1), turn], cutoff, "test", 0);

        var today = CodexRhythmToday.Calculate(history, cutoff, offset, totalTokens: 123);

        Assert.Equal(hasTurnBeforeCutoff ? 0 : null, today.Day.Messages);
        Assert.Equal(hasTurnBeforeCutoff ? 0 : null, today.Day.ClockHours);
        Assert.Equal(123L, today.TotalTokens);
    }
}
