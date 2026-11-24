using Xunit;

namespace CodexTokenMonitor.Core.Tests;

public sealed class CodexRhythmWindowAnalysisTests
{
    private static readonly TimeSpan Offset = TimeSpan.FromHours(8);
    private static readonly DateOnly Date = new(2026, 11, 21);

    [Fact]
    public void PairsTokensWithTheEarliestPeakWindowUsingHalfOpenBoundsAndInstantComparisons()
    {
        var history = History(
            [
                At(Date.AddDays(-1), 12), At(Date, 8).ToUniversalTime(), At(Date, 8, 10), At(Date, 9),
                At(Date, 19), At(Date, 19, 10), At(Date.AddDays(1), 12)
            ]);
        var analysis = Calculate(history, new Dictionary<DateOnly, long> { [Date] = 655 },
            [
                new(At(Date, 19), 500), new(At(Date, 9).ToUniversalTime(), 80),
                new(At(Date, 7, 59), 5), new(At(Date, 8).ToOffset(TimeSpan.FromHours(-3)), 10),
                new(At(Date, 8, 15), 20), new(At(Date, 8, 59), 40)
            ]);

        Assert.NotNull(analysis);
        Assert.Equal(Date, analysis.Start);
        Assert.Equal(Date, analysis.End);
        Assert.Equal(new[] { 1, 2, 4, 8, 12, 16, 24 }, analysis.Points.Select(point => point.Hours));
        Assert.Equal(new CodexRhythmWindowPoint(1, 1, 2, 2, 1, 70), analysis.Points[0]);
        Assert.Equal(new CodexRhythmWindowPoint(2, 1, 3, 1.5, 1, 150), analysis.Points[1]);
        Assert.Equal(new CodexRhythmWindowPoint(24, 1, 5, 5d / 24, 1, 655), analysis.Points[^1]);
    }

    [Fact]
    public void ClampsLatePeakToTheNaturalDayAndDoesNotCountTheNextMidnight()
    {
        var tomorrow = Date.AddDays(1);
        var history = History(
            [
                At(Date.AddDays(-1), 12), At(Date, 23, 36), At(Date, 23, 42), At(Date, 23, 59),
                At(tomorrow, 0), At(tomorrow, 0, 1)
            ]);
        var analysis = Calculate(history, new Dictionary<DateOnly, long> { [Date] = 31 },
            [
                new(At(Date, 22, 59), 1), new(At(Date, 23).ToUniversalTime(), 10),
                new(At(Date, 23, 59), 20), new(At(tomorrow, 0).ToUniversalTime(), 40)
            ]);

        Assert.NotNull(analysis);
        Assert.Equal(new CodexRhythmWindowPoint(1, 1, 3, 3, 1, 30), analysis.Points[0]);
        Assert.Equal(new CodexRhythmWindowPoint(2, 1, 3, 1.5, 1, 31), analysis.Points[1]);
    }

    [Fact]
    public void UsesTheLast56CalendarDaysAndKeepsMessageAndVerifiedTokenSamplesSeparate()
    {
        var first = Date.AddDays(-55);
        var before = first.AddDays(-1);
        var unknown = first.AddDays(1);
        var history = History(
            [
                At(before.AddDays(-1), 12), At(before, 12), At(first, 9), At(first, 9, 10),
                At(first, 9, 20), At(first, 9, 30), At(Date.AddDays(1), 12)
            ],
            [At(Date, 12)]);
        var analysis = Calculate(history, new Dictionary<DateOnly, long>
            {
                [before] = 900,
                [unknown] = 700,
                [Date] = 530
            },
            [
                new(At(before, 12), 900), new(At(first, 9), 800), new(At(unknown, 12), 700),
                new(At(Date, 1), 30), new(At(Date, 12), 500)
            ]);

        Assert.NotNull(analysis);
        Assert.Equal(first, analysis.Start);
        Assert.Equal(Date, analysis.End);
        Assert.Equal(new CodexRhythmWindowPoint(1, 2, 2, 2, 1, 0), analysis.Points[0]);
        Assert.Equal(new CodexRhythmWindowPoint(2, 2, 2, 1, 1, 30), analysis.Points[1]);
        Assert.Equal(new CodexRhythmWindowPoint(24, 2, 2, 2d / 24, 1, 530), analysis.Points[^1]);
    }

    [Fact]
    public void KeepsUnknownTokenUsageEmptyWhileStillSummarizingMessages()
    {
        var history = History([At(Date.AddDays(-1), 12), At(Date, 9), At(Date.AddDays(1), 12)]);
        var analysis = Calculate(history, new Dictionary<DateOnly, long>(), [new(At(Date, 9), 100)]);

        Assert.NotNull(analysis);
        Assert.All(analysis.Points, point =>
        {
            Assert.Equal(1, point.MessageSampleDays);
            Assert.Equal(1, point.MedianMessages);
            Assert.Equal(0, point.TokenSampleDays);
            Assert.Null(point.MedianTokens);
        });
    }

    private static CodexMessageHistory History(DateTimeOffset[] messages, DateTimeOffset[]? turns = null) =>
        new(messages, turns ?? [], messages[^1], "test", 0);

    private static CodexRhythmWindowAnalysis? Calculate(
        CodexMessageHistory history, IReadOnlyDictionary<DateOnly, long> dailyTokens,
        IReadOnlyList<CodexRhythmTokenEvent> tokenEvents) =>
        CodexRhythmWindowAnalysis.Calculate(history, CodexUsageRhythm.Calculate(history, Offset), Offset, dailyTokens, tokenEvents);

    private static DateTimeOffset At(DateOnly day, int hour, int minute = 0) =>
        new(day.ToDateTime(new TimeOnly(hour, minute)), Offset);
}
