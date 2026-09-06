using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class UsageModelTests
{
    [Fact]
    public void TokenBucket_AddTracksTotalsAndCacheRatio()
    {
        var timestamp = new DateTimeOffset(2026, 7, 13, 8, 0, 0, TimeSpan.FromHours(8));
        var bucket = new TokenUsageBucket { StartLocal = timestamp };

        bucket.Add(timestamp, input: 1_000, cached: 750, output: 200, reasoning: 50, total: 1_200);

        Assert.Equal(1, bucket.Events);
        Assert.Equal(1_000, bucket.InputTokens);
        Assert.Equal(750, bucket.CachedInputTokens);
        Assert.Equal(250, bucket.UncachedInputTokens);
        Assert.Equal(200, bucket.OutputTokens);
        Assert.Equal(50, bucket.ReasoningOutputTokens);
        Assert.Equal(1_200, bucket.TotalTokens);
        Assert.Equal(75d, bucket.CacheRatioPercent, precision: 6);
    }

    [Fact]
    public void TokenBucket_ClampsMalformedCountersWithoutNegativeCost()
    {
        var timestamp = new DateTimeOffset(2026, 7, 13, 8, 0, 0, TimeSpan.FromHours(8));
        var bucket = new TokenUsageBucket { StartLocal = timestamp };

        bucket.Add(timestamp, input: -10, cached: 500, output: -20, reasoning: -1, total: -30);

        Assert.Equal(1, bucket.Events);
        Assert.Equal(0, bucket.InputTokens);
        Assert.Equal(0, bucket.CachedInputTokens);
        Assert.Equal(0, bucket.UncachedInputTokens);
        Assert.Equal(0, bucket.OutputTokens);
        Assert.Equal(0, bucket.ReasoningOutputTokens);
        Assert.Equal(0, bucket.TotalTokens);
        Assert.Equal(0m, bucket.EstimateCost(new PriceProfile("test", "¥", 1m, 1m, 1m, 1_000_000m)));

        bucket.Add(timestamp, input: 100, cached: 250, output: 10, reasoning: 2, total: 1);

        Assert.Equal(100, bucket.InputTokens);
        Assert.Equal(100, bucket.CachedInputTokens);
        Assert.Equal(0, bucket.UncachedInputTokens);
        Assert.Equal(110, bucket.TotalTokens);
        Assert.Equal(100d, bucket.CacheRatioPercent, precision: 6);
    }

    [Fact]
    public void EstimateCost_NormalizesMalformedBucketAndIgnoresInvalidPricing()
    {
        var bucket = new TokenUsageBucket
        {
            InputTokens = 100,
            CachedInputTokens = 250,
            UncachedInputTokens = 999,
            OutputTokens = 10,
            TotalTokens = 1
        };
        var profile = new PriceProfile("malformed", "$", -1m, 2m, 3m, 0m);

        var cost = bucket.EstimateCost(profile);

        Assert.Equal(0.00023m, cost);
        Assert.Equal(100, bucket.CachedInputTokens);
        Assert.Equal(0, bucket.UncachedInputTokens);
        Assert.Equal(110, bucket.TotalTokens);
    }

    [Fact]
    public void EstimateCost_SaturatesInsteadOfThrowingOnExtremePrice()
    {
        var timestamp = new DateTimeOffset(2026, 7, 13, 8, 0, 0, TimeSpan.FromHours(8));
        var bucket = new TokenUsageBucket { StartLocal = timestamp };
        bucket.Add(timestamp, long.MaxValue, 0, long.MaxValue, 0, long.MaxValue);

        var cost = bucket.EstimateCost(new PriceProfile(
            "extreme",
            "$",
            decimal.MaxValue,
            decimal.MaxValue,
            decimal.MaxValue,
            1m));

        Assert.Equal(decimal.MaxValue, cost);
    }

    [Fact]
    public void UsageEventMerger_NormalizesCountersBeforeDeduplication()
    {
        var timestamp = new DateTimeOffset(2026, 7, 13, 9, 0, 0, TimeSpan.FromHours(8));
        var merged = UsageEventMerger.Merge(new[]
        {
            new TokenUsageEvent(timestamp, 100, 200, 10, 0, 110, null),
            new TokenUsageEvent(timestamp, 100, 100, 10, 0, 110, null)
        });

        var item = Assert.Single(merged);
        Assert.Equal(100, item.CachedInputTokens);
        Assert.Equal(0, item.CachedInputTokens > item.InputTokens ? 1 : 0);
    }

    [Fact]
    public void ReadDetailRowsForRange_SplitsPartialDaysAndPreservesOrder()
    {
        var beijing = TimeSpan.FromHours(8);
        var start = new DateTimeOffset(2026, 7, 13, 18, 30, 0, beijing);
        var end = new DateTimeOffset(2026, 7, 15, 6, 15, 0, beijing);
        var calls = new List<(DateTimeOffset Start, DateTimeOffset End)>();

        var rows = UsageBreakdownBuilder.ReadDetailRowsForRange(
            start,
            end,
            (dayStart, dayEnd) =>
            {
                calls.Add((dayStart, dayEnd));
                return new[] { new TokenUsageBucket { StartLocal = dayStart } };
            });

        Assert.Equal(
            new[]
            {
                (start, new DateTimeOffset(2026, 7, 14, 0, 0, 0, beijing)),
                (new DateTimeOffset(2026, 7, 14, 0, 0, 0, beijing), new DateTimeOffset(2026, 7, 15, 0, 0, 0, beijing)),
                (new DateTimeOffset(2026, 7, 15, 0, 0, 0, beijing), end)
            },
            calls);
        Assert.Equal(calls.Select(item => item.Start), rows.Select(row => row.StartLocal));
    }

    [Fact]
    public void CountEvents_SaturatesInsteadOfOverflowing()
    {
        var count = UsageBreakdownBuilder.CountEvents(new[]
        {
            new TokenUsageBucket { Events = long.MaxValue },
            new TokenUsageBucket { Events = long.MaxValue }
        });

        Assert.Equal(long.MaxValue, count);
    }

    [Fact]
    public void TokenBucket_MergeRecomputesDerivedCountersAfterSaturation()
    {
        var first = new TokenUsageBucket
        {
            Events = 1,
            InputTokens = long.MaxValue - 1,
            CachedInputTokens = long.MaxValue - 1,
            UncachedInputTokens = 0,
            OutputTokens = long.MaxValue - 1,
            TotalTokens = long.MaxValue,
            LongContextEvents = 1,
            LongContextInputTokens = long.MaxValue - 1,
            LongContextCachedInputTokens = long.MaxValue - 1,
            LongContextOutputTokens = long.MaxValue - 1,
            PeakInputTokens = long.MaxValue - 1,
            PeakCachedInputTokens = long.MaxValue - 1,
            PeakOutputTokens = long.MaxValue - 1
        };
        var second = new TokenUsageBucket
        {
            Events = 1,
            InputTokens = 10,
            CachedInputTokens = 0,
            UncachedInputTokens = 10,
            OutputTokens = 10,
            TotalTokens = 20,
            LongContextEvents = 1,
            LongContextInputTokens = 10,
            LongContextCachedInputTokens = 0,
            LongContextOutputTokens = 10,
            PeakInputTokens = 10,
            PeakCachedInputTokens = 0,
            PeakOutputTokens = 10
        };

        first.MergeFrom(second);

        Assert.Equal(long.MaxValue, first.InputTokens);
        Assert.Equal(long.MaxValue - 1, first.CachedInputTokens);
        Assert.Equal(1, first.UncachedInputTokens);
        Assert.Equal(long.MaxValue, first.OutputTokens);
        Assert.Equal(long.MaxValue, first.TotalTokens);
        Assert.Equal(2, first.LongContextEvents);
        Assert.Equal(long.MaxValue, first.LongContextInputTokens);
        Assert.Equal(long.MaxValue - 1, first.LongContextCachedInputTokens);
        Assert.Equal(long.MaxValue, first.LongContextOutputTokens);
        Assert.Equal(long.MaxValue, first.PeakInputTokens);
        Assert.Equal(long.MaxValue - 1, first.PeakCachedInputTokens);
        Assert.Equal(long.MaxValue, first.PeakOutputTokens);
    }

    [Fact]
    public void UsageEventMerger_PrefersMoreCompleteDuplicate()
    {
        var timestamp = new DateTimeOffset(2026, 7, 13, 9, 0, 0, TimeSpan.FromHours(8));
        var sparse = new TokenUsageEvent(timestamp, 100, 80, 0, 0, 100, "same");
        var complete = new TokenUsageEvent(timestamp, 100, 80, 20, 5, 120, "same");

        var merged = UsageEventMerger.Merge(new[] { sparse, complete });

        var item = Assert.Single(merged);
        Assert.Equal(20, item.OutputTokens);
        Assert.Equal(5, item.ReasoningOutputTokens);
        Assert.Equal(120, item.TotalTokens);
    }

    [Fact]
    public void UsageEventMerger_ComparesExtremeCountersWithoutOverflow()
    {
        var timestamp = new DateTimeOffset(2026, 7, 13, 9, 0, 0, TimeSpan.FromHours(8));
        var sparse = new TokenUsageEvent(
            timestamp,
            long.MaxValue,
            0,
            long.MaxValue,
            0,
            long.MaxValue,
            "extreme");
        var complete = sparse with
        {
            CachedInputTokens = long.MaxValue,
            ReasoningOutputTokens = long.MaxValue
        };

        var merged = UsageEventMerger.Merge(new[] { sparse, complete });

        var item = Assert.Single(merged);
        Assert.Equal(long.MaxValue, item.CachedInputTokens);
        Assert.Equal(long.MaxValue, item.ReasoningOutputTokens);
    }

    [Fact]
    public void QuotaFreshness_RejectsOldSnapshot()
    {
        var now = new DateTimeOffset(2026, 7, 13, 10, 0, 0, TimeSpan.FromHours(8));

        Assert.True(QuotaFreshness.IsFresh(now.AddMinutes(-5), now));
        Assert.False(QuotaFreshness.IsFresh(now.AddHours(-7), now));
    }

    [Fact]
    public void EstimateCost_DeepSeekScheduleCombinesPeakAndOffPeakEvents()
    {
        var bucket = new TokenUsageBucket();
        var offPeak = new DateTimeOffset(2026, 8, 17, 5, 0, 0, TimeSpan.FromHours(8));
        var peak = new DateTimeOffset(2026, 8, 17, 9, 0, 0, TimeSpan.FromHours(8));
        bucket.Add(offPeak, input: 2_000_000, cached: 1_000_000, output: 1_000_000, reasoning: 0, total: 3_000_000);
        bucket.Add(peak, input: 2_000_000, cached: 1_000_000, output: 1_000_000, reasoning: 0, total: 3_000_000);
        var profile = new PriceProfile(
            "DeepSeek V4 Flash",
            "¥",
            1.50m,
            0.05m,
            4.50m,
            1_000_000m,
            PriceSchedule.DeepSeekBeijingPeakDouble);

        var cost = bucket.EstimateCost(profile);

        Assert.Equal(18.15m, cost);
    }

    [Theory]
    [InlineData(8, 59, false)]
    [InlineData(9, 0, true)]
    [InlineData(11, 59, true)]
    [InlineData(12, 0, false)]
    [InlineData(13, 59, false)]
    [InlineData(14, 0, true)]
    [InlineData(17, 59, true)]
    [InlineData(18, 0, false)]
    public void DeepSeekSchedule_UsesOfficialBeijingBoundaries(int hour, int minute, bool expectedPeak)
    {
        var timestamp = new DateTimeOffset(2026, 8, 17, hour, minute, 0, TimeSpan.FromHours(8));

        Assert.Equal(expectedPeak, DeepSeekPricingSchedule.IsPeak(timestamp));
    }
}
