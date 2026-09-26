using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class QuotaModelTokenCapacityCalculatorTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 23, 8, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public void FullQuotaIsExpressedInMillionTokensForEveryModel()
    {
        var report = QuotaModelTokenCapacityCalculator.Calculate(Result(),
            Capacities(Estimate("gpt-5.6-sol", 2400m), Estimate("gpt-6-astra", 1400m),
                Estimate("gpt-6-sol", 1200m)), Presets());

        Assert.Equal(480m, report.Rows.Single(item => item.ModelId == "gpt-5.6-sol").FullQuotaMillionTokens);
        Assert.Equal(140m, report.Rows.Single(item => item.ModelId == "gpt-6-astra").FullQuotaMillionTokens);
        Assert.Equal(600m, report.Rows.Single(item => item.ModelId == "gpt-6-sol").FullQuotaMillionTokens);
    }

    [Fact]
    public void Sol6AloneNeedsNoReferenceModelOrAttributedQuotaShares()
    {
        var result = Result(cached: 950_000, output: 10_000);
        var capacities = Capacities(Estimate("gpt-6-sol", 1200m));
        var report = QuotaModelTokenCapacityCalculator.Calculate(result, capacities, Presets());
        var row = Assert.Single(report.Rows);

        // 1.01M tokens cost $0.39: uncached $0.10 + cached $0.19 + output $0.10.
        Assert.Equal("gpt-6-sol", row.ModelId);
        Assert.InRange(row.FullQuotaMillionTokens, 3107.69m, 3107.70m);
        var differentShares = result with
        {
            Models = new[] { new QuotaCycleModelShare("gpt-6-sol", 0m, 0m, 10, 0m, 0m, true) }
        };
        Assert.Equal(row, Assert.Single(QuotaModelTokenCapacityCalculator.Calculate(
            differentShares, capacities, Presets()).Rows));
    }

    [Fact]
    public void MoreCachedTokensIncreaseFullQuotaCapacityWithoutChangingTotalTokens()
    {
        var capacities = Capacities(Estimate("gpt-6-astra", 1400m));
        var uncached = Assert.Single(QuotaModelTokenCapacityCalculator.Calculate(
            Result(), capacities, Presets()).Rows);
        var cached = Assert.Single(QuotaModelTokenCapacityCalculator.Calculate(
            Result(cached: 1_000_000), capacities, Presets()).Rows);

        Assert.Equal(140m, uncached.FullQuotaMillionTokens);
        Assert.Equal(1400m, cached.FullQuotaMillionTokens);
    }

    [Fact]
    public void CacheReadsAndWritesAreIncludedInInputRatherThanCountedAgain()
    {
        var report = QuotaModelTokenCapacityCalculator.Calculate(
            Result(cached: 600_000, writes: 200_000, output: 100_000),
            Capacities(Estimate("gpt-6-sol", 1200m)), Presets());

        // Cost is $0.40 uncached + $0.12 cached + $0.50 writes + $1 output.
        Assert.Equal(1_100_000, report.TotalTokens);
        Assert.InRange(Assert.Single(report.Rows).FullQuotaMillionTokens, 653.46m, 653.47m);
    }

    [Fact]
    public void CapacityRangeUsesTheSameTokenPriceAndReportsMissingModels()
    {
        var result = Result() with
        {
            Models = new[]
            {
                new QuotaCycleModelShare("gpt-6-sol", 1, 90, 900_000, 1, 90, true),
                new QuotaCycleModelShare("gpt-6-astra", 1, 10, 100_000, 1, 10, true)
            }
        };
        var estimate = Estimate("gpt-6-sol", 1200m) with
        {
            MinimumFullQuotaCost = 1000m, MaximumFullQuotaCost = 1400m
        };
        var report = QuotaModelTokenCapacityCalculator.Calculate(result, Capacities(estimate), Presets());
        var row = Assert.Single(report.Rows);

        Assert.Equal(500m, row.MinimumMillionTokens);
        Assert.Equal(600m, row.FullQuotaMillionTokens);
        Assert.Equal(700m, row.MaximumMillionTokens);
        Assert.Equal("gpt-6-astra", Assert.Single(report.UnestimatedModels));
    }

    [Fact]
    public void EmptyMixAndZeroCostCannotProduceAnInfiniteCapacity()
    {
        var capacities = Capacities(Estimate("gpt-6-sol", 1200m));
        var empty = QuotaModelTokenCapacityCalculator.Calculate(
            Result(input: 0), capacities, Presets());
        var zero = QuotaModelTokenCapacityCalculator.Calculate(Result(), capacities, new[]
        {
            new PricePreset { Provider = "OpenAI", ModelId = "gpt-6-sol", CurrencySymbol = "$",
                UncachedInput = 0m, CachedInput = 0m, CacheWriteInput = 0m, Output = 0m }
        });
        Assert.Empty(empty.Rows);
        Assert.Contains("Token 构成", empty.UnavailableReason);
        Assert.Empty(zero.Rows);
        Assert.Contains("价格", zero.UnavailableReason);
    }

    [Fact]
    public void ReportedTotalUsesTheSameCountingBasisAsTheUsageTable()
    {
        var result = Result() with
        {
            UsageSamples = new[] { new QuotaCycleUsageSample(Start, 1_000_000, 0, 0, 0, 1_200_000, 1) }
        };
        var report = QuotaModelTokenCapacityCalculator.Calculate(result,
            Capacities(Estimate("gpt-6-sol", 1200m)), Presets());
        Assert.Equal(1_200_000, report.TotalTokens);
        Assert.Equal(720m, Assert.Single(report.Rows).FullQuotaMillionTokens, precision: 8);
    }

    private static QuotaCycleAnalysisResult Result(
        long input = 1_000_000, long cached = 0, long writes = 0, long output = 0) =>
        QuotaCycleAnalysisResult.Empty(
            new CodexQuotaCycle(Start, Start.AddDays(7), Start.AddDays(7), 2, 5m, true), "") with
        {
            UsageSamples = new[] { new QuotaCycleUsageSample(Start, input, cached, writes, output,
                input + output, 1) }
        };

    private static QuotaModelCapacityReport Capacities(params QuotaModelCapacityEstimate[] estimates) =>
        new("Pro 20X", estimates);

    private static QuotaModelCapacityEstimate Estimate(string model, decimal fullCost) =>
        new(model, 3, fullCost, fullCost, fullCost,
            QuotaModelCapacitySource.CurrentPeriodApproved, Start);

    private static PricePreset[] Presets() =>
    [
        new() { Provider = "OpenAI", ModelId = "gpt-5.6-sol", CurrencySymbol = "$",
            UncachedInput = 4m, CachedInput = .4m, CacheWriteInput = 5m, Output = 20m },
        new() { Provider = "OpenAI", ModelId = "gpt-6-astra", CurrencySymbol = "$",
            UncachedInput = 10m, CachedInput = 1m, CacheWriteInput = 12.5m, Output = 50m },
        new() { Provider = "OpenAI", ModelId = "gpt-6-sol", CurrencySymbol = "$",
            UncachedInput = 2m, CachedInput = .2m, CacheWriteInput = 2.5m, Output = 10m }
    ];
}
