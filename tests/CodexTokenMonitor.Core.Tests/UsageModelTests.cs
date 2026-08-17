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
