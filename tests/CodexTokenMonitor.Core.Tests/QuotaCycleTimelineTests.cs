using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class QuotaCycleTimelineTests
{
    private static readonly DateTimeOffset Start = new(2020, 1, 2, 8, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public void BuildFromSamplesPreservesRealTimesPausesAndOpenTailWithoutChangingCostBands()
    {
        // Feed an unordered source: the six-hour pause must remain six hours,
        // and the final unchanged observation must extend the visible plateau.
        var result = QuotaCycleAnalysisCalculator.BuildFromSamples(
            Period(),
            new[]
            {
                Sample(Start.AddHours(6).AddMinutes(2), 5m, 10),
                Sample(Start, 0m),
                Sample(Start.AddHours(7), 5m, 10),
                Sample(Start.AddMinutes(10), 0m, 10),
                Sample(Start.AddHours(6), 0m, 10)
            },
            priceCatalog: Catalog());

        Assert.Equal(
            new[] { Start, Start.AddMinutes(10), Start.AddHours(6), Start.AddHours(6).AddMinutes(2), Start.AddHours(7) },
            result.Timeline.Select(point => point.TimestampLocal));
        Assert.Equal(new[] { 0m, 0m, 0m, 5m, 5m }, result.Timeline.Select(point => point.UsedPercent));
        Assert.Equal(TimeSpan.FromHours(5).Add(TimeSpan.FromMinutes(50)), result.Timeline[2].TimestampLocal - result.Timeline[1].TimestampLocal);

        var band = Assert.Single(result.Bands);
        Assert.Equal(5m, result.ObservedQuotaDropPercent);
        Assert.Equal(40L, result.Tokens);
        Assert.Equal(40m, result.EquivalentCost);
        Assert.Equal(800m, result.EstimatedFullQuotaCost);
        Assert.Equal(Start.AddHours(7), band.EndLocal);
    }

    [Fact]
    public void BuildFromSamplesPreservesFlatTimelineEvenWhenThereAreNoAttributableBands()
    {
        var result = QuotaCycleAnalysisCalculator.BuildFromSamples(
            Period(),
            new[]
            {
                Sample(Start, 12m),
                Sample(Start.AddHours(1), 12m, 10),
                Sample(Start.AddHours(4), 12m, 10)
            },
            priceCatalog: Array.Empty<PricePreset>());

        Assert.False(result.HasData);
        Assert.Empty(result.Bands);
        Assert.Contains("没有可归因", result.EmptyReason);
        Assert.Equal(new[] { Start, Start.AddHours(1), Start.AddHours(4) }, result.Timeline.Select(point => point.TimestampLocal));
        Assert.All(result.Timeline, point => Assert.Equal(12m, point.UsedPercent));
    }

    [Fact]
    public void BuildFromSamplesKeepsTimelineWhenTheModelHasNoKnownPrice()
    {
        var result = QuotaCycleAnalysisCalculator.BuildFromSamples(
            Period(),
            new[] { Sample(Start, 0m), Sample(Start.AddHours(1), 5m, 10) },
            priceCatalog: Array.Empty<PricePreset>());

        Assert.Equal(new[] { 0m, 5m }, result.Timeline.Select(point => point.UsedPercent));
        Assert.Equal(0m, result.EquivalentCost);
        Assert.False(Assert.Single(result.Models).IsPriced);
    }

    [Fact]
    public void BuildTimelineCoalescesDuplicateTimesAndRetainsRegressionPlateaus()
    {
        var cutoff = Start.AddMinutes(60);
        var timeline = QuotaCycleAnalysisCalculator.BuildTimeline(
            new[]
            {
                Sample(Start, decimal.MinValue),
                Sample(Start.AddMinutes(10), 10m),
                Sample(Start.AddMinutes(10), 9m),
                Sample(Start.AddMinutes(10), 12m),
                Sample(Start.AddMinutes(20), 11m),
                Sample(Start.AddMinutes(30), 12.00005m),
                Sample(Start.AddMinutes(40), 12.0002m),
                new QuotaCycleAnalysisSample(Start.AddMinutes(45), 80m, null!),
                Sample(Start.AddMinutes(50), decimal.MaxValue),
                Sample(cutoff, 90m),
                Sample(cutoff.AddSeconds(1), 100m)
            },
            cutoff);

        Assert.Equal(
            new[] { Start, Start.AddMinutes(10), Start.AddMinutes(20), Start.AddMinutes(30), Start.AddMinutes(40), Start.AddMinutes(50), cutoff },
            timeline.Select(point => point.TimestampLocal));
        Assert.Equal(new[] { 0m, 12m, 12m, 12m, 12.0002m, 100m, 100m }, timeline.Select(point => point.UsedPercent));
        Assert.All(timeline, point => Assert.InRange(point.UsedPercent, 0m, 100m));
        Assert.DoesNotContain(timeline, point => point.TimestampLocal > cutoff);
    }

    [Fact]
    public void BuildFromSamplesKeepsTheAuthoritativeLiveAnchorBeforeTheDetectedPeriod()
    {
        var period = Period() with { PeriodStart = Start.AddHours(1) };
        var result = QuotaCycleAnalysisCalculator.BuildFromSamples(
            period,
            new[] { Sample(Start, 0m), Sample(Start.AddHours(2), 5m, 10) },
            priceCatalog: Catalog());

        Assert.Equal(Start, result.Timeline[0].TimestampLocal);
        Assert.Equal(0m, result.Timeline[0].UsedPercent);
        Assert.Equal(Start.AddHours(2), result.Timeline[^1].TimestampLocal);
    }

    [Fact]
    public void BuildFromSamplesRetainsOneAnchorWithoutInventingAnEndPoint()
    {
        var result = QuotaCycleAnalysisCalculator.BuildFromSamples(
            Period(),
            new[] { Sample(Start.AddHours(2), 7m) },
            priceCatalog: Catalog());

        Assert.False(result.HasData);
        Assert.Contains("至少需要两个", result.EmptyReason);
        Assert.Equal(new QuotaCycleTimelinePoint(Start.AddHours(2), 7m), Assert.Single(result.Timeline));
    }

    [Fact]
    public void EmptyAndFutureOnlySourcesDoNotInventTimelinePoints()
    {
        var result = QuotaCycleAnalysisCalculator.BuildFromSamples(
            Period(), Array.Empty<QuotaCycleAnalysisSample>(), priceCatalog: Catalog());

        Assert.False(result.HasData);
        Assert.Empty(result.Timeline);
        Assert.Empty(QuotaCycleAnalysisResult.Empty(Period(), "test").Timeline);
        Assert.Empty(QuotaCycleAnalysisCalculator.BuildTimeline(
            new[] { Sample(Start.AddSeconds(1), 20m) }, Start));
    }

    private static CodexQuotaCycle Period() => new(Start, Start.AddDays(7), Start.AddDays(7), 10, 20m, false);

    private static QuotaCycleAnalysisSample Sample(DateTimeOffset timestamp, decimal used, long tokens = 0)
    {
        var bucket = new TokenUsageBucket { StartLocal = timestamp };
        if (tokens > 0)
        {
            bucket.Add(new TokenUsageEvent(timestamp, tokens, 0, 0, 0, tokens,
                ModelId: "timeline-test-model", ServiceTier: "default"));
        }
        return new QuotaCycleAnalysisSample(timestamp, used, bucket);
    }

    private static IReadOnlyList<PricePreset> Catalog() =>
    [
        new()
        {
            Group = PricePresetGroups.Codex,
            Provider = "OpenAI",
            Model = "timeline-test-model",
            ModelId = "timeline-test-model",
            CurrencySymbol = "$",
            UnitLabel = "$ / token",
            Divisor = 1m,
            UncachedInput = 1m,
            CachedInput = 1m,
            CacheWriteInput = 1m,
            Output = 1m,
            Source = "test"
        }
    ];
}
