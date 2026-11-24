using Xunit;

namespace CodexTokenMonitor.Core.Tests;

public sealed class CodexRhythmDailyTrendPointTests
{
    private static readonly DateOnly Start = new(2026, 11, 1);

    [Fact]
    public void RawKeepsDatesAndUnknownOrZeroMetrics()
    {
        var points = CodexRhythmDailyTrendPoint.Create(
            [Day(0, 0, 10), Day(2, 1, 0), Day(3, null, 8), Day(4, 9, null)], movingAverage: false);

        Assert.Equal(
            new CodexRhythmDailyTrendPoint[]
            {
                new(Start, 0, 10, null), new(Start.AddDays(2), 1, 0, 0),
                new(Start.AddDays(3), null, 8, null), new(Start.AddDays(4), 9, null, null)
            },
            points);
    }

    [Fact]
    public void AveragesSevenDaysAndDividesWindowTotalsIncludingKnownZeroMessageDays()
    {
        var points = CodexRhythmDailyTrendPoint.Create(
            [Day(0, 0, 70), Day(1, 1, 7), Day(2, 1, 7), Day(3, 1, 7), Day(4, 1, 7), Day(5, 1, 7), Day(6, 2, 140)],
            movingAverage: true);

        Assert.All(points[..6], point => Assert.Equal(new(point.Date, null, null, null), point));
        Assert.Equal(new(Start.AddDays(6), 1, 35, 35), points[6]);
    }

    [Theory]
    [InlineData(null, 14L, null, 14d)]
    [InlineData(2, null, 2d, null)]
    public void MissingCoverageDisablesOnlyItsMeanAndThePairedRatio(
        int? messages, long? tokens, double? expectedMessages, double? expectedTokens)
    {
        var days = Enumerable.Range(0, 6).Select(i => Day(i, 2, 14)).Append(Day(6, messages, tokens)).ToArray();
        var point = CodexRhythmDailyTrendPoint.Create(days, movingAverage: true)[^1];

        Assert.Equal(new(Start.AddDays(6), expectedMessages, expectedTokens, null), point);
    }

    [Fact]
    public void RestartsTheWindowAfterAnAbsentDateAndKeepsRatioBlankWhenTheDenominatorIsZero()
    {
        var days = Enumerable.Range(0, 6).Concat(Enumerable.Range(7, 7))
            .Select(i => Day(i, 0, long.MaxValue)).ToArray();
        var points = CodexRhythmDailyTrendPoint.Create(days, movingAverage: true);

        Assert.All(points[..^1], point => Assert.Equal(new(point.Date, null, null, null), point));
        Assert.Equal(new(Start.AddDays(13), 0, (double)long.MaxValue, null), points[^1]);
    }

    private static CodexRhythmDailyUsage Day(int offset, int? messages, long? tokens) =>
        new(new(Start.AddDays(offset), messages, null, null, null), tokens);
}
