using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class QuotaForecastCalculatorTests
{
    private static readonly DateTimeOffset Start = new(2020, 1, 2, 8, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public void ObservedForecastIncludesIdleAndStartsAtLastRealPointInsteadOfNow()
    {
        var analysis = Analysis(new[] { Point(0, 20m), Point(50, 20m), Point(60, 40m) });
        var now = Start.AddMinutes(69);
        var target = Start.AddHours(6);

        var result = Build(analysis, target: target, now: now);

        Assert.Equal(QuotaForecastStatus.Ready, result.Status);
        Assert.Equal(Start, result.StartLocal);
        Assert.Equal(Start.AddHours(1), result.LastSampleLocal);
        Assert.Equal(TimeSpan.FromMinutes(9), result.SampleAge);
        Assert.Equal(TimeSpan.FromHours(1), result.SampleDuration);
        Assert.Equal(20m, result.ObservedRatePerHour);
        Assert.Equal(60m, result.CurrentRemainingPercent);
        Assert.Equal(Start.AddHours(4), result.Current.RunoutAtLocal);
        Assert.Equal(0m, result.Current.RemainingAtResetPercent);
        Assert.False(result.Current.EnoughUntilReset);
        Assert.Equal(60m, result.Current.MaxActivityPercent);

        var half = Build(analysis, target: target, now: now, activity: 50m);
        Assert.Equal(20m, half.ObservedRatePerHour);
        Assert.Equal(20m, half.Current.BaseRatePerHour);
        Assert.Equal(10m, half.Current.RatePerHour);
        Assert.Equal(Start.AddHours(7), half.Current.RunoutAtLocal);
        Assert.Equal(10m, half.Current.RemainingAtResetPercent);
        Assert.True(half.Current.EnoughUntilReset);
        Assert.Equal(60m, half.Current.MaxActivityPercent);
    }

    [Fact]
    public void RecentWindowUsesOnlyCoveredRealTimesAndDoesNotInventBoundaryAnchors()
    {
        var analysis = Analysis(new[] { Point(0, 0m), Point(46, 10m), Point(55, 16m), Point(60, 17m) });

        var result = Build(analysis, lookback: TimeSpan.FromMinutes(15));

        Assert.Equal(Start.AddMinutes(46), result.StartLocal);
        Assert.Equal(TimeSpan.FromMinutes(14), result.SampleDuration);
        Assert.Equal(3, result.SampleCount);
        Assert.Equal(30m, result.ObservedRatePerHour);
        Assert.Equal(QuotaForecastStatus.Ready, result.Status);

        var wholeCycle = Build(analysis, lookback: TimeSpan.MaxValue);
        Assert.Equal(Start, wholeCycle.StartLocal);
        Assert.Equal(17m, wholeCycle.ObservedRatePerHour);
    }

    [Fact]
    public void SameWorkloadModelsPreserveCacheAndOutputMixAndFastDoesNotBoostThroughputAgain()
    {
        var usage = new QuotaCycleUsageSample(Start.AddHours(1), 1_000_000, 800_000, 100_000,
            100_000, 1_100_000, 10);
        var analysis = Analysis(new[] { Point(0, 20m), Point(60, 40m) },
            // The first sample's tokens predate the measured (start, end] interval.
            usage with { TimestampLocal = Start, InputTokens = 999_000_000 },
            usage,
            usage with { TimestampLocal = Start.AddHours(2), InputTokens = 999_000_000 });
        var capacities = Capacities("gpt-5.6-sol", 100m);
        var apiPrice = Preset("gpt-5.6-sol", input: 4m, cached: .4m, output: 20m, write: 5m);

        var result = Build(analysis, capacities, new[] { apiPrice }, activity: 40m);
        var normal = Scenario(result, "gpt-5.6-sol", "普通");
        var fast = Scenario(result, "gpt-5.6-sol", "Fast");

        // Subscription reference is 5/.5/30 and cache write 6.25: .5+.4+.625+3.
        Assert.Equal(4.525m, normal.BaseRatePerHour);
        Assert.Equal(1.81m, normal.RatePerHour);
        Assert.Equal(normal.BaseRatePerHour * 2.5m, fast.BaseRatePerHour);
        Assert.Equal(normal.RatePerHour * 2.5m, fast.RatePerHour);
        Assert.Equal(2.5m, fast.FastMultiplier);
        Assert.Equal(1_100_000m, result.TokensPerHour);
        Assert.Equal(3, normal.SampleCount);
        Assert.Equal(QuotaModelCapacitySource.CurrentPeriodApproved, normal.CalibrationSource);
        Assert.Equal(1_000_000, usage.InputTokens);
    }

    [Theory]
    [InlineData("gpt-6-astra", "2.5")]
    [InlineData("gpt-5.6-terra", "2.5")]
    [InlineData("gpt-5.6-luna", "2.5")]
    [InlineData("gpt-5.5", "2.5")]
    [InlineData("gpt-5.4", "2")]
    public void FastScenarioUsesOnlyTheKnownQuotaMultiplier(string model, string factorText)
    {
        var analysis = Analysis(new[] { Point(0, 20m), Point(60, 40m) }, Usage(60, 1_000_000));
        var result = Build(analysis, Capacities(model, 100m), new[] { Preset(model, input: 10m) });
        var normal = Scenario(result, model, "普通");
        var fast = Scenario(result, model, "Fast");
        var factor = decimal.Parse(factorText, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(10m, normal.BaseRatePerHour);
        Assert.Equal(10m * factor, fast.BaseRatePerHour);
        Assert.Equal(factor, fast.FastMultiplier);
    }

    [Fact]
    public void MissingCalibrationPriceAndFastMultiplierRemainVisibleAndUnavailable()
    {
        var analysis = Analysis(new[] { Point(0, 20m), Point(60, 40m) }, Usage(60, 1_000_000)) with
        {
            Models = new[] { new QuotaCycleModelShare("unpriced-model", 20m, 100m, 1_000_000, 0m, 0m, false) }
        };
        var report = new QuotaModelCapacityReport("test", new[]
        {
            Capacity("unpriced-model", 100m), Capacity("regular-only-model", 100m), Capacity("zero-price-model", 100m)
        });
        var result = Build(analysis, report, new[]
        {
            Preset("regular-only-model"), Preset("no-capacity-model"), Preset("zero-price-model", 0m, 0m, 0m, 0m)
        });

        Assert.Equal(QuotaForecastStatus.MissingPrice, Scenario(result, "unpriced-model", "普通").Status);
        Assert.Equal(QuotaForecastStatus.MissingPrice, Scenario(result, "zero-price-model", "普通").Status);
        Assert.Equal(QuotaForecastStatus.MissingCalibration, Scenario(result, "no-capacity-model", "普通").Status);
        Assert.Equal(QuotaForecastStatus.Ready, Scenario(result, "regular-only-model", "普通").Status);
        Assert.Equal(QuotaForecastStatus.UnknownFastMultiplier, Scenario(result, "regular-only-model", "Fast").Status);
        foreach (var unavailable in result.Models.Where(item => item.Status != QuotaForecastStatus.Ready))
        {
            Assert.Null(unavailable.RunoutAtLocal);
            Assert.Null(unavailable.RemainingAtResetPercent);
            Assert.Null(unavailable.EnoughUntilReset);
            Assert.False(string.IsNullOrWhiteSpace(unavailable.Reason));
        }
    }

    [Fact]
    public void FlatQuotaDoesNotPromiseUnlimitedUsageButKnownWorkloadCanStillCompareModels()
    {
        var analysis = Analysis(new[] { Point(0, 40m), Point(60, 40m) }, Usage(60, 1_000_000));
        var result = Build(analysis, Capacities("gpt-5.4", 100m), new[] { Preset("gpt-5.4") });

        Assert.Equal(QuotaForecastStatus.NoQuotaDrop, result.Status);
        Assert.Equal(0m, result.ObservedRatePerHour);
        Assert.Null(result.Current.RunoutAtLocal);
        Assert.Null(result.Current.EnoughUntilReset);
        Assert.Null(result.Current.RemainingAtResetPercent);
        Assert.Equal(QuotaForecastStatus.Ready, Scenario(result, "gpt-5.4", "普通").Status);
    }

    [Theory]
    [InlineData(899, (int)QuotaForecastStatus.Ready)]
    [InlineData(900, (int)QuotaForecastStatus.StaleSamples)]
    [InlineData(1800, (int)QuotaForecastStatus.StaleSamples)]
    public void StalenessBoundaryIsFifteenMinutesWithoutAdvancingTheQuotaPoint(int seconds, int expectedStatus)
    {
        var last = Start.AddHours(1);
        var expected = (QuotaForecastStatus)expectedStatus;
        var result = Build(Analysis(new[] { Point(0, 20m), Point(60, 40m) }), now: last.AddSeconds(seconds));

        Assert.Equal(expected, result.Status);
        Assert.Equal(last, result.LastSampleLocal);
        Assert.Equal(60m, result.CurrentRemainingPercent);
        if (expected == QuotaForecastStatus.StaleSamples)
        {
            Assert.Null(result.Current.RunoutAtLocal);
            Assert.Null(result.Current.EnoughUntilReset);
        }
    }

    [Fact]
    public void HistoricalAndPastTargetAndInsufficientDataNeverProducePredictions()
    {
        var analysis = Analysis(new[] { Point(0, 20m), Point(60, 40m) });
        var historical = Build(analysis with { Period = analysis.Period with { IsCurrent = false } });
        var past = Build(analysis, target: Start.AddHours(1));
        var tooShort = Build(Analysis(new[] { Point(58, 20m), Point(60, 40m) }));
        var single = Build(Analysis(new[] { Point(60, 40m) }));
        var empty = Build(Analysis(Array.Empty<QuotaCycleTimelinePoint>()));

        Assert.Equal(QuotaForecastStatus.HistoricalPeriod, historical.Status);
        Assert.Equal(QuotaForecastStatus.TargetInPast, past.Status);
        Assert.Equal(QuotaForecastStatus.InsufficientSamples, tooShort.Status);
        Assert.Equal(QuotaForecastStatus.InsufficientSamples, single.Status);
        Assert.Equal(QuotaForecastStatus.NoSamples, empty.Status);
        Assert.All(new[] { historical, past, tooShort, single, empty }, result =>
        {
            Assert.Null(result.Current.RunoutAtLocal);
            Assert.Null(result.Current.RemainingAtResetPercent);
            Assert.Null(result.Current.MaxActivityPercent);
            Assert.Null(result.Current.EnoughUntilReset);
        });
    }

    [Fact]
    public void DuplicateTimesAreMergedAndFuturePointsExcludedWhileRegressionsAreRejected()
    {
        var result = Build(Analysis(new[]
        {
            Point(60, 40m), Point(60, 39m), Point(0, 20m), Point(61, 99m)
        }));
        Assert.Equal(2, result.SampleCount);
        Assert.Equal(20m, result.ObservedRatePerHour);
        Assert.Equal(Start.AddHours(1), result.LastSampleLocal);
        Assert.Equal(QuotaForecastStatus.InvalidData, Build(Analysis(new[] { Point(0, 50m), Point(60, 40m) })).Status);
        var extremePercent = Build(Analysis(new[] { Point(0, decimal.MinValue), Point(60, decimal.MaxValue) }));
        Assert.Equal(QuotaForecastStatus.InvalidData, extremePercent.Status);
        Assert.Null(extremePercent.Current.RunoutAtLocal);
    }

    [Fact]
    public void TimeStampedUsageSnapshotsDoNotRetainOrMutateSourceBuckets()
    {
        var source = new TokenUsageBucket
        {
            StartLocal = Start.AddHours(1), InputTokens = 100, CachedInputTokens = 500,
            CacheWriteInputTokens = -20, OutputTokens = 10, Events = 2
        };
        var snapshot = QuotaCycleUsageSample.From(source);
        var result = Build(Analysis(new[] { Point(0, 20m), Point(60, 40m) }, snapshot),
            Capacities("gpt-5.4", 100m), new[] { Preset("gpt-5.4") });

        Assert.Equal(500, source.CachedInputTokens);
        Assert.Equal(-20, source.CacheWriteInputTokens);
        Assert.Equal(0, source.TotalTokens);
        Assert.Equal(110m, result.TokensPerHour);
        source.InputTokens = 999;
        Assert.Equal(100, snapshot.InputTokens);
    }

    [Fact]
    public void BuildFromSamplesCarriesUsageThroughFlatAndEmptyAnalysisResults()
    {
        var sampleUsage = new TokenUsageBucket { StartLocal = Start.AddHours(1) };
        sampleUsage.Add(Start.AddHours(1), 100, 50, 10, 0, 110);
        var result = QuotaCycleAnalysisCalculator.BuildFromSamples(Period(), new[]
        {
            new QuotaCycleAnalysisSample(Start, 10m, new TokenUsageBucket { StartLocal = Start }),
            new QuotaCycleAnalysisSample(Start.AddHours(1), 10m, sampleUsage)
        }, priceCatalog: Array.Empty<PricePreset>());

        Assert.False(result.HasData);
        Assert.Equal(2, result.UsageSamples.Count);
        Assert.Equal(100, result.UsageSamples[1].InputTokens);
        sampleUsage.InputTokens = 999;
        Assert.Equal(100, result.UsageSamples[1].InputTokens);
        Assert.Empty(QuotaCycleAnalysisResult.Empty(Period(), "empty").UsageSamples);
    }

    [Fact]
    public void InvalidControlsAndExtremeRatesDoNotReturnWrappedDates()
    {
        var analysis = Analysis(new[] { Point(0, 20m), Point(60, 40m) }, Usage(60, 1_000_000));
        Assert.Throws<ArgumentOutOfRangeException>(() => Build(analysis, lookback: TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => Build(analysis, activity: 0m));
        Assert.Throws<ArgumentOutOfRangeException>(() => Build(analysis, activity: 101m));
        var extreme = Build(analysis, Capacities("gpt-5.4", 0.0000000000000000000000000001m),
            new[] { Preset("gpt-5.4", decimal.MaxValue) });
        Assert.All(extreme.Models, item => Assert.Null(item.RunoutAtLocal));
        var extremeRate = Build(analysis, Capacities("gpt-5.4", 0.0000000000000000000000000001m),
            new[] { Preset("gpt-5.4", 10m) });
        Assert.All(extremeRate.Models, item => Assert.Equal(QuotaForecastStatus.InvalidData, item.Status));
    }

    [Fact]
    public void AlreadyExhaustedQuotaHasNoRemainingWorkloadBudget()
    {
        var result = Build(Analysis(new[] { Point(0, 30m), Point(60, 100m) }));

        Assert.Equal(0m, result.CurrentRemainingPercent);
        Assert.Equal(Start.AddHours(1), result.Current.RunoutAtLocal);
        Assert.Equal(0m, result.Current.MaxActivityPercent);
        Assert.Equal(0m, result.Current.RemainingAtResetPercent);
        Assert.False(result.Current.EnoughUntilReset);

        var flatEmpty = Build(Analysis(new[] { Point(0, 100m), Point(60, 100m) }));
        Assert.Equal(QuotaForecastStatus.NoQuotaDrop, flatEmpty.Status);
        Assert.Equal(0m, flatEmpty.CurrentRemainingPercent);
        Assert.Null(flatEmpty.Current.EnoughUntilReset);
    }

    [Fact]
    public void VeryDistantRunoutKeepsBoundedTargetEstimateWithoutWrappingTheDate()
    {
        var analysis = Analysis(new[] { Point(0, 20m), Point(60, 40m) }, Usage(60, 1_000_000));
        var result = Build(analysis, Capacities("gpt-5.4", 1_000_000_000m),
            new[] { Preset("gpt-5.4", .1m) });
        var normal = Scenario(result, "gpt-5.4", "普通");

        Assert.Equal(QuotaForecastStatus.Ready, normal.Status);
        Assert.Equal(.00000001m, normal.RatePerHour);
        Assert.Null(normal.RunoutAtLocal);
        Assert.Contains("日期范围", normal.Reason);
        Assert.InRange(normal.RemainingAtResetPercent!.Value, 59m, 60m);
        Assert.True(normal.EnoughUntilReset);
    }

    [Fact]
    public void HittingZeroExactlyAtTargetCountsAsReachingTheTarget()
    {
        var result = Build(Analysis(new[] { Point(0, 20m), Point(60, 40m) }), target: Start.AddHours(4));

        Assert.Equal(result.TargetResetLocal, result.Current.RunoutAtLocal);
        Assert.Equal(0m, result.Current.RemainingAtResetPercent);
        Assert.True(result.Current.EnoughUntilReset);
        Assert.Equal(100m, result.Current.MaxActivityPercent);
    }

    [Fact]
    public void UsageAfterTheLastQuotaPointIsNotCountedEvenWhenItIsBeforeNow()
    {
        var analysis = Analysis(new[] { Point(0, 20m), Point(60, 40m) },
            Usage(60, 1_000_000), Usage(65, 999_000_000));
        var result = Build(analysis, Capacities("gpt-5.4", 100m), new[] { Preset("gpt-5.4", 10m) },
            now: Start.AddMinutes(69));

        Assert.Equal(1_000_000m, result.TokensPerHour);
        Assert.Equal(10m, Scenario(result, "gpt-5.4", "普通").RatePerHour);
        Assert.Equal(Start.AddHours(1), result.LastSampleLocal);
    }

    [Fact]
    public void MissingTokenBreakdownCannotSilentlyUnderpriceTheModelScenario()
    {
        var noUsage = Build(Analysis(new[] { Point(0, 20m), Point(60, 40m) }),
            Capacities("gpt-5.4", 100m), new[] { Preset("gpt-5.4") });
        Assert.Equal(QuotaForecastStatus.Ready, noUsage.Current.Status);
        Assert.Equal(QuotaForecastStatus.NoUsage, Scenario(noUsage, "gpt-5.4", "普通").Status);

        var partialUsage = Build(Analysis(new[] { Point(0, 20m), Point(60, 40m) },
            Usage(60, 1_000_000) with { TotalTokens = 2_000_000 }),
            Capacities("gpt-5.4", 100m), new[] { Preset("gpt-5.4") });
        Assert.Equal(50m, partialUsage.UsageCoveragePercent);
        Assert.Equal(QuotaForecastStatus.IncompleteUsage, Scenario(partialUsage, "gpt-5.4", "普通").Status);
        Assert.Null(Scenario(partialUsage, "gpt-5.4", "普通").RatePerHour);
    }

    [Theory]
    [InlineData(1_000_000L, "10")]
    [InlineData(1_040_000L, "10.4")]
    public void HighlyCoveredUsageExtendsTheKnownMixToSmallUnclassifiedRemainders(long total, string expectedText)
    {
        var usage = Usage(60, 1_000_000) with { TotalTokens = total };
        var result = Build(Analysis(new[] { Point(0, 20m), Point(60, 40m) }, usage),
            Capacities("gpt-5.4", 100m), new[] { Preset("gpt-5.4", 10m) });
        var normal = Scenario(result, "gpt-5.4", "普通");

        Assert.Equal(QuotaForecastStatus.Ready, normal.Status);
        Assert.Equal(decimal.Parse(expectedText, System.Globalization.CultureInfo.InvariantCulture), normal.BaseRatePerHour);
        Assert.Equal(result.UsageCoveragePercent, normal.UsageCoveragePercent);
        Assert.Equal(1_000_000m / total * 100m, result.UsageCoveragePercent);
        if (total > 1_000_000) Assert.Contains("未分类按已知比例估算", normal.Source);
        Assert.Equal(1_000_000, usage.InputTokens);
        Assert.Equal(total, usage.TotalTokens);
    }

    [Fact]
    public void TotalOnlyEventsCannotManufactureAModelComposition()
    {
        var result = Build(Analysis(new[] { Point(0, 20m), Point(60, 40m) }, Usage(60, 0) with { TotalTokens = 1_000_000 }),
            Capacities("gpt-5.4", 100m), new[] { Preset("gpt-5.4") });
        Assert.Equal(QuotaForecastStatus.Ready, result.Current.Status);
        Assert.Equal(0m, result.UsageCoveragePercent);
        Assert.Equal(QuotaForecastStatus.NoUsage, Scenario(result, "gpt-5.4", "普通").Status);
        Assert.Null(Scenario(result, "gpt-5.4", "普通").RatePerHour);
    }

    private static QuotaForecastResult Build(QuotaCycleAnalysisResult analysis,
        QuotaModelCapacityReport? capacities = null, IEnumerable<PricePreset>? catalog = null,
        TimeSpan? lookback = null, DateTimeOffset? target = null, DateTimeOffset? now = null,
        decimal activity = 100m) => QuotaForecastCalculator.Build(analysis,
            capacities ?? new QuotaModelCapacityReport("test", Array.Empty<QuotaModelCapacityEstimate>()),
            lookback ?? TimeSpan.FromHours(1), target ?? Start.AddHours(6), activity,
            now ?? Start.AddHours(1), catalog ?? Array.Empty<PricePreset>());

    private static QuotaForecastScenario Scenario(QuotaForecastResult result, string model, string mode) =>
        Assert.Single(result.Models, item => item.ModelId == model && item.Mode == mode);

    private static QuotaCycleTimelinePoint Point(int minute, decimal used) => new(Start.AddMinutes(minute), used);
    private static QuotaCycleUsageSample Usage(int minute, long input) => new(Start.AddMinutes(minute), input, 0, 0, 0, input, 1);
    private static CodexQuotaCycle Period() => new(Start, Start.AddDays(7), Start.AddDays(7), 3, 40m, true);
    private static QuotaCycleAnalysisResult Analysis(IReadOnlyList<QuotaCycleTimelinePoint> points,
        params QuotaCycleUsageSample[] usage) => QuotaCycleAnalysisResult.Empty(Period(), "test") with
        { Timeline = points, UsageSamples = usage };
    private static QuotaModelCapacityEstimate Capacity(string model, decimal amount) => new(model, 3, amount,
        amount, amount, QuotaModelCapacitySource.CurrentPeriodApproved, Start);
    private static QuotaModelCapacityReport Capacities(string model, decimal amount) => new("test", new[] { Capacity(model, amount) });
    private static PricePreset Preset(string model, decimal input = 5m, decimal cached = .5m,
        decimal output = 30m, decimal write = 6.25m) => new()
    {
        Model = model, ModelId = model, Provider = "OpenAI", CurrencySymbol = "$", Divisor = 1_000_000m,
        UncachedInput = input, CachedInput = cached, CacheWriteInput = write, Output = output
    };
}
