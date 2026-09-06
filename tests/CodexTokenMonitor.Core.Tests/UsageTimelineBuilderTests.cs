using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class UsageTimelineBuilderTests
{
    private static readonly TimeSpan BeijingOffset = TimeSpan.FromHours(8);

    [Fact]
    public void Build_WithIntervalAggregatesRowsIntoTimelineBuckets()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, BeijingOffset);
        var rows = new[]
        {
            Bucket(start.AddMinutes(1), 10, 4, 2),
            Bucket(start.AddMinutes(9), 20, 8, 3),
            Bucket(start.AddMinutes(11), 30, 12, 5)
        };

        var result = UsageTimelineBuilder.Build(
            start,
            start.AddHours(1),
            rows,
            TimeSpan.FromMinutes(10));

        Assert.Equal(2, result.Count);
        Assert.Equal(start, result[0].StartLocal);
        Assert.Equal(2, result[0].Events);
        Assert.Equal(35, result[0].TotalTokens);
        Assert.Equal(12, result[0].CachedInputTokens);
        Assert.Equal(start.AddMinutes(10), result[1].StartLocal);
        Assert.Equal(1, result[1].Events);
        Assert.Equal(35, result[1].TotalTokens);
    }

    [Fact]
    public void Build_WithoutIntervalPreservesSortedEventRows()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, BeijingOffset);
        var later = Bucket(start.AddMinutes(2), 1, 0, 1);
        var earlier = Bucket(start.AddMinutes(1), 1, 0, 2);

        var result = UsageTimelineBuilder.Build(
            start,
            start.AddHours(1),
            new[] { later, earlier },
            null);

        Assert.Equal(new[] { earlier, later }, result);
    }

    [Fact]
    public void Build_ClampsRowsOutsideRangeToVisibleBuckets()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, BeijingOffset);
        var result = UsageTimelineBuilder.Build(
            start,
            start.AddMinutes(20),
            new[]
            {
                Bucket(start.AddMinutes(-5), 1, 0, 7),
                Bucket(start.AddMinutes(25), 1, 0, 9)
            },
            TimeSpan.FromMinutes(10));

        Assert.Equal(2, result.Count);
        Assert.Equal(start, result[0].StartLocal);
        Assert.Equal(start.AddMinutes(10), result[1].StartLocal);
        Assert.Equal(8, result[0].TotalTokens);
        Assert.Equal(10, result[1].TotalTokens);
    }

    [Fact]
    public void Build_NormalizesNonBeijingRange()
    {
        var dayStart = new DateTimeOffset(2026, 8, 20, 0, 0, 0, BeijingOffset);
        var row = new TokenUsageBucket { StartLocal = dayStart.AddHours(1), Events = 1, TotalTokens = 10 };

        var result = UsageTimelineBuilder.Build(
            dayStart.ToUniversalTime(),
            dayStart.AddHours(2).ToUniversalTime(),
            new[] { row },
            TimeSpan.FromHours(1));

        Assert.Equal(dayStart.AddHours(1), Assert.Single(result).StartLocal);
    }

    private static TokenUsageBucket Bucket(
        DateTimeOffset start,
        long input,
        long cached,
        long output)
    {
        var bucket = new TokenUsageBucket { StartLocal = start };
        bucket.Add(start, input, cached, output, 0, input + output);
        return bucket;
    }
}
