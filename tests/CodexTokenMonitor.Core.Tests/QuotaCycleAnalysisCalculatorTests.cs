using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class QuotaCycleAnalysisCalculatorTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 1, 8, 0, 0, TimeSpan.FromHours(8));

    [Theory]
    [InlineData(1, 10)]
    [InlineData(2, 5)]
    [InlineData(5, 2)]
    [InlineData(10, 1)]
    public void SelectedBandSizePartitionsTheSameQuotaDrop(int bandSize, int expectedBands)
    {
        var samples = new[]
        {
            Sample(0, 0m, Bucket("model-a", 1)), Sample(1, 10m, Bucket("model-a", 100))
        };
        var result = QuotaCycleAnalysisCalculator.BuildFromSamples(
            Period(),
            samples,
            bandSizePercent: bandSize,
            priceCatalog: Catalog());
        var fivePercent = QuotaCycleAnalysisCalculator.BuildFromSamples(
            Period(), samples, bandSizePercent: 5m, priceCatalog: Catalog());

        Assert.Equal(expectedBands, result.Bands.Count);
        Assert.Equal(bandSize, result.BandSizePercent);
        Assert.Equal(10m, result.ObservedQuotaDropPercent);
        Assert.InRange(Math.Abs(result.EquivalentCost - fivePercent.EquivalentCost), 0m, 0.000001m);
        Assert.Equal(0m, result.Bands[0].UsedFromPercent);
        Assert.Equal(10m, result.Bands[^1].UsedToPercent);
        Assert.All(result.Bands, band => Assert.Equal((decimal)bandSize, band.QuotaDropPercent));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(10)]
    public void SmallerOrLargerBandsDoNotInventQuotaDrops(int bandSize)
    {
        var result = QuotaCycleAnalysisCalculator.BuildFromSamples(
            Period(),
            new[] { Sample(0, 0m, Bucket("model-a", 1)), Sample(1, 0m, Bucket("model-a", 100)) },
            bandSizePercent: bandSize,
            priceCatalog: Catalog());

        Assert.False(result.HasData);
        Assert.Equal(bandSize, result.BandSizePercent);
        Assert.Contains("没有可归因的额度下降", result.EmptyReason);
    }

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
    public void FailedPlanReadCannotOverwriteModelCalibrationAndRecoversOnNextBuild()
    {
        var root = Path.Combine(Path.GetTempPath(), "CalibrationSettings-" + Guid.NewGuid().ToString("N"));
        using var caches = MonitorCachePaths.PushLocalAppDataRoot(root);
        using var logs = UsageLogPaths.PushRoot(Path.Combine(root, "logs"));
        var period = Period();
        SubscriptionPlanStore.Save(new[] { new SubscriptionPlanRecord
        {
            Id = "probe-plan", StartLocal = period.PeriodStart, EndLocal = period.PeriodEnd.AddDays(1),
            PlanName = "Probe plan", AmountCny = 1234m
        } });
        QuotaModelCapacityCalibrationStore.Upsert("Probe plan", period,
            new[] { CapacityEstimate("gpt-6-astra", 999m, period.PeriodStart) });
        SetPlanStart("invalid date");
        var result = ResultWithBands(CapacityBand(0, "gpt-6-astra", 100m, 1350m));
        using (var failed = CacheOperationDiagnostics.Begin())
        {
            QuotaModelCapacityCalibrationService.Build(period, result, previousPeriod: null, CancellationToken.None);
            Assert.NotEmpty(failed.Warnings);
            Assert.Equal(999m, ReadCapacity());
        }
        SetPlanStart(period.PeriodStart.ToString("O"));
        using var recovered = CacheOperationDiagnostics.Begin();
        QuotaModelCapacityCalibrationService.Build(period, result, previousPeriod: null, CancellationToken.None);
        Assert.Empty(recovered.Warnings);
        Assert.Equal(1350m, ReadCapacity());

        decimal ReadCapacity() => Assert.Single(QuotaModelCapacityCalibrationStore.LoadExact(
            "Probe plan", period.PeriodStart, QuotaModelCapacitySource.CurrentPeriodApproved)).AverageFullQuotaCost;
        static void SetPlanStart(string value)
        {
            using var connection = MonitorSettingsDatabase.OpenConnection(MonitorSettingsDatabase.Path);
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE subscription_plans SET start_local = $start WHERE id = 'probe-plan'";
            command.Parameters.AddWithValue("$start", value);
            command.ExecuteNonQuery();
        }
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
    public void HistoricalPriorUsesAllPeriodsAndSuppressesAnOutlier()
    {
        var history = new[]
        {
            HistoricalEstimate("gpt-5.6-sol", 980m, Start.AddDays(-28)),
            HistoricalEstimate("gpt-5.6-sol", 1_000m, Start.AddDays(-21)),
            HistoricalEstimate("gpt-5.6-sol", 1_050m, Start.AddDays(-14)),
            HistoricalEstimate("gpt-5.6-sol", 10_000m, Start.AddDays(-7))
        };

        var estimate = Assert.Single(RobustQuotaModelCapacityEstimator.BuildHistoricalPriors(history, Start));

        Assert.InRange(estimate.AverageFullQuotaCost, 900m, 1_300m);
        Assert.InRange(estimate.MinimumFullQuotaCost, 750m, 1_200m);
        Assert.InRange(estimate.MaximumFullQuotaCost, 950m, 1_500m);
        Assert.Equal(4, estimate.HistoricalPeriodCount);
        Assert.Equal(QuotaModelCapacitySource.PreviousPeriodApproved, estimate.Source);
    }

    [Fact]
    public void RobustRegressionSeparatesModelsAcrossDifferentMixtures()
    {
        var result = ResultWithBands(
            RegressionBand(0, 5m, ("model-a", 40m), ("model-b", 20m)),
            RegressionBand(1, 5m, ("model-a", 10m), ("model-b", 80m)));
        var baselines = new[]
        {
            CapacityEstimate("model-a", 1_300m, Start),
            CapacityEstimate("model-b", 1_600m, Start)
        };

        var estimates = RobustQuotaModelCapacityEstimator.RegressCurrentPeriod(result, baselines);

        Assert.Equal(2, estimates.Count);
        Assert.InRange(estimates.Single(item => item.ModelId == "model-a").AverageFullQuotaCost, 900m, 1_150m);
        Assert.InRange(estimates.Single(item => item.ModelId == "model-b").AverageFullQuotaCost, 1_750m, 2_200m);
        Assert.All(estimates, item => Assert.Equal(QuotaModelCapacitySource.CurrentPeriodBlended, item.Source));
    }

    [Fact]
    public void LowShareModelRemainsPriorDominated()
    {
        var result = ResultWithBands(
            RegressionBand(0, 5m, ("model-a", 49.95m), ("model-b", 0.05m)));
        var baselines = new[]
        {
            HistoricalEstimate("model-a", 1_000m, Start.AddDays(-7)),
            HistoricalEstimate("model-b", 2_000m, Start.AddDays(-7))
        };

        var estimate = RobustQuotaModelCapacityEstimator.RegressCurrentPeriod(result, baselines)
            .Single(item => item.ModelId == "model-b");

        Assert.InRange(estimate.AverageFullQuotaCost, 1_900m, 2_100m);
        Assert.Equal(1, estimate.HistoricalPeriodCount);
    }

    [Fact]
    public void RobustRegressionDownweightsAHighCostOutlierBand()
    {
        var result = ResultWithBands(
            RegressionBand(0, 5m, ("model-a", 50m)),
            RegressionBand(1, 5m, ("model-a", 50m)),
            RegressionBand(2, 5m, ("model-a", 50m)),
            RegressionBand(3, 5m, ("model-a", 50m)),
            RegressionBand(4, 5m, ("model-a", 50m)),
            RegressionBand(5, 5m, ("model-a", 200m)));
        var baseline = new[] { CapacityEstimate("model-a", 1_000m, Start) };

        var estimate = Assert.Single(RobustQuotaModelCapacityEstimator.RegressCurrentPeriod(result, baseline));

        Assert.InRange(estimate.AverageFullQuotaCost, 850m, 1_250m);
    }

    [Fact]
    public void RobustRegressionRejectsBandsWithUnpricedQuotaShare()
    {
        var priced = RegressionBand(0, 5m, ("model-a", 50m));
        var band = priced with
        {
            Models = priced.Models.Concat(new[]
            {
                new QuotaCycleModelShare("unknown", 0.1m, 2m, 1, 0m, 0m, false)
            }).ToList()
        };

        var estimates = RobustQuotaModelCapacityEstimator.RegressCurrentPeriod(
            ResultWithBands(band),
            new[] { CapacityEstimate("model-a", 1_000m, Start) });

        Assert.Empty(estimates);
    }

    [Fact]
    public void FitFullQuotaCostUsesEachModelsCostShare()
    {
        var band = MixedCapacityBand(
            1_600m,
            ("gpt-6-astra", 50m),
            ("gpt-5.6-sol", 30m),
            ("gpt-5.6-luna", 20m));
        var capacities = new[]
        {
            CapacityEstimate("gpt-6-astra", 2_000m, Start),
            CapacityEstimate("gpt-5.6-sol", 1_000m, Start),
            CapacityEstimate("gpt-5.6-luna", 500m, Start)
        };

        var fitted = QuotaModelCapacityEstimator.FitFullQuotaCost(band, capacities);

        Assert.NotNull(fitted);
        Assert.Equal(1_052.632m, fitted.Value, 3);
    }

    [Fact]
    public void CalibrationBuildUsesLatestPriorValuesAndDynamicallyBlendsAllModels()
    {
        var root = Path.Combine(Path.GetTempPath(), "QuotaModelFallback-" + Guid.NewGuid().ToString("N"));
        using var caches = MonitorCachePaths.PushLocalAppDataRoot(root);
        using var logs = UsageLogPaths.PushRoot(Path.Combine(root, "logs"));
        var current = Period();
        var older = current with
        {
            PeriodStart = current.PeriodStart.AddDays(-14),
            PeriodEnd = current.PeriodStart.AddDays(-7),
            ResetAt = current.PeriodStart.AddDays(-7)
        };
        SubscriptionPlanStore.Save(new[] { new SubscriptionPlanRecord
        {
            Id = "plan", StartLocal = older.PeriodStart, EndLocal = current.PeriodEnd.AddDays(1),
            PlanName = "Pro 20x", AmountCny = 1_380m
        } });
        QuotaModelCapacityCalibrationStore.Upsert("Pro 20x", older, new[]
        {
            CapacityEstimate("gpt-5.6-sol", 1_000m, older.PeriodStart),
            CapacityEstimate("gpt-5.6-luna", 500m, older.PeriodStart)
        });
        var mixed = MixedCapacityBand(
            1_600m,
            ("gpt-6-astra", 50m),
            ("gpt-5.6-sol", 30m),
            ("gpt-5.6-luna", 20m));
        var result = ResultWithBandsAndModels(
            new[] { CapacityBand(0, "gpt-6-astra", 100m, 2_000m), mixed },
            mixed.Models.ToArray());

        var report = QuotaModelCapacityCalibrationService.Build(current, result, previousPeriod: null);

        Assert.Equal(3, report.Estimates.Count);
        Assert.All(report.Estimates, item => Assert.Equal(QuotaModelCapacitySource.CurrentPeriodBlended, item.Source));
        Assert.All(report.Estimates, item => Assert.Equal(current.PeriodStart, item.CalibrationPeriodStart));
        Assert.NotEqual(1_000m, report.Estimates.Single(item => item.ModelId == "gpt-5.6-sol").AverageFullQuotaCost);
        Assert.NotEqual(500m, report.Estimates.Single(item => item.ModelId == "gpt-5.6-luna").AverageFullQuotaCost);
    }

    [Fact]
    public void CalibrationBuildCarriesAllEarlierPeriodsIntoTheCurrentRegression()
    {
        var root = Path.Combine(Path.GetTempPath(), "QuotaModelHistory-" + Guid.NewGuid().ToString("N"));
        using var caches = MonitorCachePaths.PushLocalAppDataRoot(root);
        using var logs = UsageLogPaths.PushRoot(Path.Combine(root, "logs"));
        var current = Period();
        SubscriptionPlanStore.Save(new[] { new SubscriptionPlanRecord
        {
            Id = "plan", StartLocal = current.PeriodStart.AddDays(-28), EndLocal = current.PeriodEnd.AddDays(1),
            PlanName = "Pro 20x", AmountCny = 1_380m
        } });
        foreach (var (days, capacity) in new[] { (-21, 950m), (-14, 1_000m), (-7, 1_050m) })
        {
            var historical = current with
            {
                PeriodStart = current.PeriodStart.AddDays(days),
                PeriodEnd = current.PeriodStart.AddDays(days + 7),
                ResetAt = current.PeriodStart.AddDays(days + 7)
            };
            QuotaModelCapacityCalibrationStore.Upsert("Pro 20x", historical,
                new[] { CapacityEstimate("model-a", capacity, historical.PeriodStart) });
        }

        var currentBand = CapacityBand(0, "model-a", 100m, 1_000m);
        var report = QuotaModelCapacityCalibrationService.Build(
            current,
            ResultWithBandsAndModels(new[] { currentBand }, currentBand.Models.Single()),
            previousPeriod: null);

        var estimate = Assert.Single(report.Estimates);
        Assert.Equal(3, estimate.HistoricalPeriodCount);
        Assert.Equal(1, estimate.CurrentBandCount);
        Assert.Equal(1, estimate.BandCount);
        Assert.Equal(QuotaModelCapacitySource.CurrentPeriodBlended, estimate.Source);
        Assert.InRange(estimate.AverageFullQuotaCost, 950m, 1_050m);
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

        var latest = Assert.Single(QuotaModelCapacityCalibrationStore.LoadLatestBefore(
            "Pro 20x",
            Start,
            new[] { "gpt-5.6-sol" },
            QuotaModelCapacitySource.PreviousPeriodApproved));
        Assert.Equal(2_400m, latest.AverageFullQuotaCost);
        Assert.Equal(previous.PeriodStart, latest.CalibrationPeriodStart);

        var history = QuotaModelCapacityCalibrationStore.LoadHistoryBefore(
            "PRO 20X",
            Start,
            new[] { "gpt-5.6-sol" },
            QuotaModelCapacitySource.PreviousPeriodApproved);
        Assert.Equal(2, history.Count);
        Assert.Equal(new[] { 1_000m, 2_400m }, history.Select(item => item.AverageFullQuotaCost));
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

    private static QuotaCycleAnalysisBand MixedCapacityBand(
        decimal observedFullQuotaCost,
        params (string Model, decimal CostSharePercent)[] models)
    {
        const decimal drop = 5m;
        var totalCost = observedFullQuotaCost * drop / 100m;
        return new QuotaCycleAnalysisBand(
            1,
            5m,
            10m,
            Start.AddMinutes(1),
            Start.AddMinutes(2),
            drop,
            100,
            totalCost,
            observedFullQuotaCost,
            models[0].Model,
            models.Select(item => new QuotaCycleModelShare(
                item.Model,
                drop * item.CostSharePercent / 100m,
                item.CostSharePercent,
                1,
                totalCost * item.CostSharePercent / 100m,
                item.CostSharePercent,
                true)).ToList());
    }

    private static QuotaCycleAnalysisBand RegressionBand(
        int index,
        decimal drop,
        params (string Model, decimal Cost)[] models)
    {
        var totalCost = models.Sum(item => item.Cost);
        return new QuotaCycleAnalysisBand(
            index,
            index * drop,
            (index + 1) * drop,
            Start.AddMinutes(index),
            Start.AddMinutes(index + 1),
            drop,
            100,
            totalCost,
            totalCost / drop * 100m,
            models.OrderByDescending(item => item.Cost).First().Model,
            models.Select(item => new QuotaCycleModelShare(
                item.Model,
                drop * item.Cost / totalCost,
                item.Cost / totalCost * 100m,
                1,
                item.Cost,
                item.Cost / totalCost * 100m,
                true)).ToList());
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

    private static QuotaModelCapacityEstimate HistoricalEstimate(
        string model,
        decimal value,
        DateTimeOffset periodStart) => new(
        model,
        4,
        value,
        value * 0.9m,
        value * 1.1m,
        QuotaModelCapacitySource.PreviousPeriodApproved,
        periodStart,
        HistoricalPeriodCount: 1);

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
