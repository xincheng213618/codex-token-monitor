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
    public void BuildFromSamplesIncludesOpenTailInLatestQuotaBand()
    {
        var result = QuotaCycleAnalysisCalculator.BuildFromSamples(
            Period(),
            new[]
            {
                Sample(0, 0m, Bucket("model-a", 0)),
                Sample(1, 2m, Bucket("model-a", 10)),
                Sample(2, 2m, Bucket("model-b", 20))
            },
            priceCatalog: Catalog());

        var band = Assert.Single(result.Bands);
        Assert.Equal(30m, band.EquivalentCost);
        Assert.Equal(1_500m, band.EstimatedFullQuotaCost);
        Assert.Equal(30, band.Tokens);
        Assert.Equal(Start.AddMinutes(2), band.EndLocal);
        Assert.Equal("model-b", band.DominantModel);
        Assert.Equal(20, band.Models.Single(item => item.ModelId == "model-b").Tokens);
    }

    [Fact]
    public void EstimatePureModelsTreatsNinetyNinePercentAsPureAndAveragesBands()
    {
        var result = ResultWithBands(
            CapacityBand(0, "gpt-5.6-sol", 100m, 2_000m),
            CapacityBand(1, "gpt-5.6-sol", 98.6m, 2_400m),
            CapacityBand(2, "gpt-5.6-sol", 98.4m, 8_000m));

        var estimate = Assert.Single(QuotaModelCapacityEstimator.EstimatePureModels(result));

        Assert.Equal("gpt-5.6-sol", estimate.ModelId);
        Assert.Equal(2, estimate.BandCount);
        Assert.Equal(2_200m, estimate.AverageFullQuotaCost);
        Assert.Equal(2_000m, estimate.MinimumFullQuotaCost);
        Assert.Equal(2_400m, estimate.MaximumFullQuotaCost);
        Assert.Equal(QuotaModelCapacitySource.CurrentPeriodApproved, estimate.Source);
    }

    [Fact]
    public void EstimatePureModelsAcceptsOnePureBand()
    {
        var result = ResultWithBands(
            CapacityBand(0, "gpt-6-astra", 100m, 1_350m));

        var estimate = Assert.Single(QuotaModelCapacityEstimator.EstimatePureModels(result));
        Assert.Equal(1_350m, estimate.AverageFullQuotaCost);
        Assert.Equal(1, estimate.BandCount);
    }

    [Fact]
    public void EstimatePureModelsIgnoresModelsThatDisplayAsZeroPercent()
    {
        var insignificant = CapacityBand(0, "codex-auto-review", 100m, 5m);
        var result = ResultWithBandsAndModels(
            new[] { insignificant },
            new QuotaCycleModelShare("codex-auto-review", 0.004m, 0.04m, 1, 0.25m, 0.04m, true));

        Assert.Empty(QuotaModelCapacityEstimator.EstimatePureModels(result));
    }

    [Fact]
    public void InferMixedModelUsesApprovedModelAsTheKnownPart()
    {
        var band = new QuotaCycleAnalysisBand(
            0,
            0m,
            5m,
            Start,
            Start.AddHours(1),
            5m,
            100,
            80m,
            1_600m,
            "gpt-5.6-sol",
            new[]
            {
                new QuotaCycleModelShare("gpt-5.6-sol", 3.75m, 75m, 75, 60m, 75m, true),
                new QuotaCycleModelShare("gpt-5.6-luna", 1.25m, 25m, 25, 20m, 25m, true)
            });
        var approved = new QuotaModelCapacityEstimate(
            "gpt-5.6-sol",
            2,
            2_400m,
            2_300m,
            2_500m,
            QuotaModelCapacitySource.CurrentPeriodApproved,
            Start);

        var estimate = Assert.Single(QuotaModelCapacityEstimator.InferMixedModels(
            ResultWithBands(band),
            new[] { approved }));

        Assert.Equal("gpt-5.6-luna", estimate.ModelId);
        Assert.Equal(800m, estimate.AverageFullQuotaCost);
        Assert.Equal(QuotaModelCapacitySource.CurrentPeriodInferred, estimate.Source);
    }

    [Fact]
    public void BlendMixedModelsUsesModelShareAsFractionalWeight()
    {
        var band = new QuotaCycleAnalysisBand(
            0,
            0m,
            5m,
            Start,
            Start.AddHours(1),
            5m,
            100,
            80m,
            1_600m,
            "gpt-5.6-sol",
            new[]
            {
                new QuotaCycleModelShare("gpt-5.6-sol", 2.5m, 50m, 50, 40m, 50m, true),
                new QuotaCycleModelShare("gpt-6-astra", 2.5m, 50m, 50, 40m, 50m, true)
            });
        var result = ResultWithBandsAndModels(
            new[] { band },
            new QuotaCycleModelShare("gpt-5.6-sol", 2.5m, 50m, 50, 40m, 50m, true),
            new QuotaCycleModelShare("gpt-6-astra", 2.5m, 50m, 50, 40m, 50m, true));
        var baselines = new[]
        {
            CapacityEstimate("gpt-5.6-sol", 1_000m, Start),
            CapacityEstimate("gpt-6-astra", 2_000m, Start)
        };

        var estimates = QuotaModelCapacityEstimator.BlendMixedModels(
            result,
            baselines,
            baselines.Select(item => item.ModelId).ToList());

        Assert.Equal(2, estimates.Count);
        var sol = estimates.Single(item => item.ModelId == "gpt-5.6-sol");
        var astra = estimates.Single(item => item.ModelId == "gpt-6-astra");
        Assert.Equal(1_066.667m, sol.AverageFullQuotaCost, 3);
        Assert.Equal(2_133.333m, astra.AverageFullQuotaCost, 3);
        Assert.Equal(1, sol.MixedBandCount);
        Assert.Equal(QuotaModelCapacitySource.CurrentPeriodBlended, sol.Source);
    }

    [Fact]
    public void CalibrationStoreLoadsOnlyTheExactRequestedPeriod()
    {
        var root = Path.Combine(Path.GetTempPath(), "QuotaModelCapacity-" + Guid.NewGuid().ToString("N"));
        using var scope = MonitorCachePaths.PushLocalAppDataRoot(root);
        var older = Period() with { PeriodStart = Start.AddDays(-14), PeriodEnd = Start.AddDays(-7) };
        var previous = Period() with { PeriodStart = Start.AddDays(-7), PeriodEnd = Start };
        QuotaModelCapacityCalibrationStore.Upsert("Pro 20x", older, new[]
        {
            CapacityEstimate("gpt-5.6-sol", 1_000m, older.PeriodStart)
        });
        QuotaModelCapacityCalibrationStore.Upsert("Pro 20x", previous, new[]
        {
            CapacityEstimate("gpt-5.6-sol", 2_400m, previous.PeriodStart)
        });

        var loaded = Assert.Single(QuotaModelCapacityCalibrationStore.LoadExact(
            "Pro 20x",
            previous.PeriodStart,
            QuotaModelCapacitySource.PreviousPeriodApproved));

        Assert.Equal(2_400m, loaded.AverageFullQuotaCost);
        Assert.Equal(previous.PeriodStart, loaded.CalibrationPeriodStart);
        Assert.Empty(QuotaModelCapacityCalibrationStore.LoadExact(
            "Pro 20x",
            Start,
            QuotaModelCapacitySource.CurrentPeriodApproved));
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

    private static QuotaCycleAnalysisResult ResultWithBands(
        params QuotaCycleAnalysisBand[] bands) => new(
        Period(),
        bands,
        Array.Empty<QuotaCycleModelShare>(),
        0,
        bands.Sum(item => item.QuotaDropPercent),
        0,
        bands.Sum(item => item.EquivalentCost),
        null,
        0m,
        0m,
        0m,
        "",
        "");

    private static QuotaCycleAnalysisResult ResultWithBandsAndModels(
        IReadOnlyList<QuotaCycleAnalysisBand> bands,
        params QuotaCycleModelShare[] models) => new(
        Period(),
        bands,
        models,
        0,
        bands.Sum(item => item.QuotaDropPercent),
        0,
        bands.Sum(item => item.EquivalentCost),
        null,
        0m,
        0m,
        0m,
        "",
        "");

    private static QuotaCycleAnalysisBand CapacityBand(
        int index,
        string model,
        decimal modelShare,
        decimal fullQuotaCost)
    {
        const decimal drop = 5m;
        var cost = fullQuotaCost * drop / 100m;
        return new QuotaCycleAnalysisBand(
            index,
            index * drop,
            (index + 1) * drop,
            Start.AddMinutes(index),
            Start.AddMinutes(index + 1),
            drop,
            1,
            cost,
            fullQuotaCost,
            model,
            new[]
            {
                new QuotaCycleModelShare(model, drop, modelShare, 1, cost, modelShare, true)
            });
    }

    private static QuotaModelCapacityEstimate CapacityEstimate(
        string model,
        decimal value,
        DateTimeOffset periodStart) => new(
        model,
        1,
        value,
        value,
        value,
        QuotaModelCapacitySource.CurrentPeriodApproved,
        periodStart);

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
