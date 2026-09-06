using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class UsageSummaryBuilderTests
{
    [Fact]
    public void FromRows_PreservesAggregatedLongContextAndPeakFields()
    {
        var start = new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.FromHours(8));
        var row = new TokenUsageBucket { StartLocal = start.AddHours(10) };
        row.Add(
            row.StartLocal,
            input: 300_000,
            cached: 200_000,
            output: 500,
            reasoning: 25,
            total: 300_500);

        var summary = UsageSummaryBuilder.FromRows(start, start.AddDays(1), new[] { row });

        Assert.Equal(row.Events, summary.Events);
        Assert.Equal(row.InputTokens, summary.InputTokens);
        Assert.Equal(row.CachedInputTokens, summary.CachedInputTokens);
        Assert.Equal(row.UncachedInputTokens, summary.UncachedInputTokens);
        Assert.Equal(row.LongContextEvents, summary.LongContextEvents);
        Assert.Equal(row.LongContextInputTokens, summary.LongContextInputTokens);
        Assert.Equal(row.LongContextCachedInputTokens, summary.LongContextCachedInputTokens);
        Assert.Equal(row.LongContextOutputTokens, summary.LongContextOutputTokens);
        Assert.Equal(row.PeakInputTokens, summary.PeakInputTokens);
        Assert.Equal(row.PeakCachedInputTokens, summary.PeakCachedInputTokens);
        Assert.Equal(row.PeakOutputTokens, summary.PeakOutputTokens);
        Assert.Equal(row.LastTokenEventLocal, summary.LastTokenEventLocal);
    }
}
