using Xunit;

namespace CodexTokenMonitor.Core.Tests;

public sealed class CodexRhythmDailyUsageTests
{
    [Fact]
    public void PreservesDayOrderAndDistinguishesMissingUsageFromZeroAndUndefinedRates()
    {
        var referenceDate = new DateOnly(2026, 11, 21);
        var stamp = new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.FromHours(8));
        var silence = new CodexSilence(stamp, stamp.AddHours(4));
        CodexRhythmDay[] days =
        [
            new(referenceDate, 4, 2, 0.5, silence),
            new(referenceDate.AddDays(-1), 3, 1, 0.25, null),
            new(referenceDate.AddDays(-3), 2, 1, 0.25, null),
            new(referenceDate.AddDays(-2), 0, 0, 0, null),
            new(referenceDate.AddDays(-4), null, null, null, null)
        ];

        var rows = CodexRhythmDailyUsage.Create(days, new Dictionary<DateOnly, long>
        {
            [days[0].Date] = long.MaxValue,
            [days[2].Date] = 0,
            [days[3].Date] = 300,
            [days[4].Date] = 400,
            [referenceDate.AddDays(1)] = 999
        });

        Assert.Equal(days.Select(day => day.Date), rows.Select(row => row.Date));
        Assert.Equal(4, rows[0].Messages);
        Assert.Equal(2, rows[0].ClockHours);
        Assert.Equal(0.5, rows[0].InteractionHours);
        Assert.Same(silence, rows[0].LongestSilence);
        Assert.Equal(long.MaxValue, rows[0].TotalTokens);
        Assert.Equal((double)long.MaxValue / 4, rows[0].TokensPerMessage);
        Assert.Null(rows[1].TotalTokens);
        Assert.Null(rows[1].TokensPerMessage);
        Assert.Equal(0, rows[2].TotalTokens);
        Assert.Equal(0, rows[2].TokensPerMessage);
        Assert.Equal(300, rows[3].TotalTokens);
        Assert.Null(rows[3].TokensPerMessage);
        Assert.Equal(400, rows[4].TotalTokens);
        Assert.Null(rows[4].TokensPerMessage);
    }
}
