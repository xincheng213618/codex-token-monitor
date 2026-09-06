using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class QuotaCycleAnalysisCalculatorTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 1, 8, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public void BuildFromSamplesCreatesFivePercentModelBandsAndVolatility()
    {
        var result = QuotaCycleAnalysisCalculator.BuildFromSamples(
            Period(),
            new[]
            {
                Sample(0, 10m, Bucket("model-a", 1)),
                Sample(1, 15m, Bucket("model-a", 10)),
                Sample(2, 20m, Bucket("model-b", 20))
            },
            priceCatalog: Catalog());

        Assert.True(result.HasData);
        Assert.Equal(2, result.Bands.Count);
        Assert.Equal(10m, result.ObservedQuotaDropPercent);
        Assert.Equal(30m, result.EquivalentCost);
        Assert.Equal(300m, result.EstimatedFullQuotaCost);
        Assert.Equal(200m, result.MinimumBandEstimate);
        Assert.Equal(400m, result.MaximumBandEstimate);
        Assert.Equal(33.333m, result.VolatilityPercent, 3);

        Assert.Equal("model-a", result.Bands[0].DominantModel);
        Assert.Equal(90m, result.Bands[0].RemainingFromPercent);
        Assert.Equal(85m, result.Bands[0].RemainingToPercent);
        Assert.Equal("model-b", result.Bands[1].DominantModel);
        Assert.Equal("model-b", result.DominantModel);
    }

    [Fact]
    public void BuildFromSamplesAttributesMixedIntervalByEquivalentCost()
    {
        var mixed = new TokenUsageBucket { StartLocal = Start.AddMinutes(1) };
        mixed.Add(Event(1, "model-a", 10));
        mixed.Add(Event(1, "model-b", 30));

        var result = QuotaCycleAnalysisCalculator.BuildFromSamples(
            Period(),
            new[]
            {
                Sample(0, 0m, Bucket("model-a", 1)),
                Sample(1, 5m, mixed)
            },
            priceCatalog: Catalog());

        var band = Assert.Single(result.Bands);
        Assert.Equal("model-b", band.DominantModel);
        Assert.Equal(2, band.Models.Count);
        Assert.Equal(25m, band.Models.Single(item => item.ModelId == "model-a").QuotaSharePercent);
        Assert.Equal(75m, band.Models.Single(item => item.ModelId == "model-b").QuotaSharePercent);
    }

    [Fact]
    public void BuildFromSamplesKeepsUsageAcrossTemporaryQuotaRegression()
    {
        var result = QuotaCycleAnalysisCalculator.BuildFromSamples(
            Period(),
            new[]
            {
                Sample(0, 10m, Bucket("model-a", 1)),
                Sample(1, 9m, Bucket("model-a", 10)),
                Sample(2, 15m, Bucket("model-a", 10))
            },
            priceCatalog: Catalog());

        var band = Assert.Single(result.Bands);
        Assert.Equal(20m, band.EquivalentCost);
        Assert.Equal(400m, band.EstimatedFullQuotaCost);
        Assert.Equal(20, band.Tokens);
    }

    [Fact]
    public void BuildFromSamplesReturnsReasonWhenAnchorsAreInsufficient()
    {
        var result = QuotaCycleAnalysisCalculator.BuildFromSamples(
            Period(),
            new[] { Sample(0, 10m, Bucket("model-a", 1)) },
            priceCatalog: Catalog());

        Assert.False(result.HasData);
        Assert.Contains("至少需要两个", result.EmptyReason);
    }

    private static CodexQuotaCycle Period() => new(
        Start,
        Start.AddDays(7),
        Start.AddDays(7),
        10,
        20m,
        false);

    private static QuotaCycleAnalysisSample Sample(int minute, decimal used, TokenUsageBucket usage) =>
        new(Start.AddMinutes(minute), used, usage);

    private static TokenUsageBucket Bucket(string model, long tokens)
    {
        var bucket = new TokenUsageBucket { StartLocal = Start };
        bucket.Add(Event(0, model, tokens));
        return bucket;
    }

    private static TokenUsageEvent Event(int minute, string model, long tokens) => new(
        Start.AddMinutes(minute),
        tokens,
        0,
        0,
        0,
        tokens,
        ModelId: model,
        ServiceTier: "default");

    private static IReadOnlyList<PricePreset> Catalog() =>
    [
        Preset("model-a"),
        Preset("model-b")
    ];

    private static PricePreset Preset(string model) => new()
    {
        Group = PricePresetGroups.Codex,
        Provider = "OpenAI",
        Model = model,
        ModelId = model,
        CurrencySymbol = "$",
        UnitLabel = "$ / token",
        Divisor = 1m,
        UncachedInput = 1m,
        CachedInput = 1m,
        CacheWriteInput = 1m,
        Output = 1m,
        Source = "test"
    };
}
