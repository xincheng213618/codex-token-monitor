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

    [Fact]
    public void Build_WithIntervalOrdersUnsortedInputAndEnumeratesItOnce()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, BeijingOffset);
        var enumerations = 0;
        IEnumerable<TokenUsageBucket> Rows()
        {
            Assert.Equal(1, ++enumerations);
            yield return Bucket(start.AddMinutes(21), 30, 12, 5);
            yield return Bucket(start.AddMinutes(9), 20, 8, 3);
            yield return new TokenUsageBucket { StartLocal = start.AddMinutes(11), Events = 0, TotalTokens = 100 };
            yield return Bucket(start.AddMinutes(1), 10, 4, 2);
        }

        var result = UsageTimelineBuilder.Build(start, start.AddHours(1), Rows(), TimeSpan.FromMinutes(10));

        Assert.Equal(1, enumerations);
        Assert.Equal(new[] { start, start.AddMinutes(20) }, result.Select(row => row.StartLocal));
        Assert.Equal(new long[] { 35, 35 }, result.Select(row => row.TotalTokens));
        Assert.Equal(new long[] { 12, 12 }, result.Select(row => row.CachedInputTokens));
        Assert.Equal(start.AddMinutes(9), result[0].LastTokenEventLocal);
    }

    [Fact]
    public void Build_WithIntervalPreservesSaturatedCounters()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, BeijingOffset);
        var first = new TokenUsageBucket
        {
            StartLocal = start.AddMinutes(1), Events = long.MaxValue - 1,
            InputTokens = long.MaxValue - 4, CachedInputTokens = long.MaxValue - 8,
            CacheWriteInputTokens = 2, OutputTokens = 2, TotalTokens = long.MaxValue - 2,
            LastTokenEventLocal = start.AddMinutes(1)
        };
        var second = Bucket(start.AddMinutes(2), 100, 50, 7);
        var third = Bucket(start.AddMinutes(3), 100, 50, 7);

        var result = Assert.Single(UsageTimelineBuilder.Build(
            start, start.AddHours(1), new[] { third, first, second }, TimeSpan.FromMinutes(10)));

        Assert.Equal(long.MaxValue, result.Events);
        Assert.Equal(long.MaxValue, result.TotalTokens);
        Assert.Equal(long.MaxValue, result.InputTokens);
        Assert.Equal(long.MaxValue, result.CachedInputTokens);
        Assert.Equal(0, result.CacheWriteInputTokens);
        Assert.Equal(0, result.UncachedInputTokens);
        Assert.Equal(16, result.OutputTokens);
        Assert.Equal(start.AddMinutes(3), result.LastTokenEventLocal);
    }

    [Fact]
    public void Build_WithIntervalUsesInstantsAndClampsThePartialFinalBucket()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, BeijingOffset);
        var end = start.AddMinutes(25);
        var result = UsageTimelineBuilder.Build(
            start.ToUniversalTime(), end.ToOffset(TimeSpan.FromHours(-5)),
            new[]
            {
                Bucket(end.ToUniversalTime(), 10, 4, 1),
                Bucket(start.AddMinutes(10).ToOffset(TimeSpan.FromHours(3)), 20, 8, 2),
                Bucket(start.ToUniversalTime(), 30, 12, 3)
            }, TimeSpan.FromMinutes(10));

        Assert.Equal(new[] { start, start.AddMinutes(10), start.AddMinutes(20) }, result.Select(row => row.StartLocal));
        Assert.All(result, row => Assert.Equal(BeijingOffset, row.StartLocal.Offset));
        Assert.Equal(new long[] { 33, 22, 11 }, result.Select(row => row.TotalTokens));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Build_WithInvalidIntervalPreservesSortedEventRows(int intervalMinutes)
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, BeijingOffset);
        var earlier = Bucket(start.AddMinutes(1), 1, 0, 1);
        var later = Bucket(start.AddMinutes(2), 1, 0, 1);

        var result = UsageTimelineBuilder.Build(start, start.AddHours(1),
            new[] { later, earlier }, TimeSpan.FromMinutes(intervalMinutes));

        Assert.Equal(new[] { earlier, later }, result);
    }

    [Fact]
    public void Build_WithIntervalAndNoEventsReturnsNoBuckets()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, BeijingOffset);
        var result = UsageTimelineBuilder.Build(start, start.AddHours(1),
            new[] { new TokenUsageBucket { StartLocal = start, TotalTokens = 100 } },
            TimeSpan.FromMinutes(10));

        Assert.Empty(result);
        Assert.Empty(UsageTimelineBuilder.Build(start, start.AddHours(1),
            Array.Empty<TokenUsageBucket>(), TimeSpan.FromMinutes(10)));
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
