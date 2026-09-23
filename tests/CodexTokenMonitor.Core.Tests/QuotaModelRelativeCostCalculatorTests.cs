using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class QuotaModelRelativeCostCalculatorTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 23, 8, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public void SameInputTokensUsePriceRatioTimesInverseFullQuotaCapacity()
    {
        var result = Result(input: 1_000_000, output: 0);
        var comparison = QuotaModelRelativeCostCalculator.Calculate(result, Capacities(), Presets());

        var astra = Assert.Single(comparison.Rows, item => item.ModelId == "gpt-6-astra");
        Assert.Equal(2m, astra.PriceRatio);
        Assert.Equal(2400m / 1400m, astra.CapacityRatio);
        Assert.InRange(astra.QuotaRatio, 3.4285m, 3.4286m);

        var sol6 = Assert.Single(comparison.Rows, item => item.ModelId == "gpt-6-sol");
        Assert.Equal(0.4m, sol6.PriceRatio);
        Assert.InRange(sol6.QuotaRatio, 0.8798m, 0.8800m);
    }

    [Fact]
    public void OutputTokensChangeTheSameWorkloadRatio()
    {
        var comparison = QuotaModelRelativeCostCalculator.Calculate(
            Result(input: 1_000_000, output: 1_000_000), Capacities(), Presets());

        var astra = Assert.Single(comparison.Rows, item => item.ModelId == "gpt-6-astra");
        Assert.Equal(60m / 35m, astra.PriceRatio);
        Assert.InRange(astra.QuotaRatio, 2.9387m, 2.9388m);
        Assert.InRange(astra.InputQuotaRatio, 3.4285m, 3.4286m);
        Assert.InRange(astra.OutputQuotaRatio, 2.8571m, 2.8572m);
    }

    [Fact]
    public void MissingReferenceEstimateDoesNotInventAMultiplier()
    {
        var capacities = new QuotaModelCapacityReport("Pro 20X",
            new[] { Estimate("gpt-6-astra", 1400m) });

        var comparison = QuotaModelRelativeCostCalculator.Calculate(
            Result(input: 1_000_000, output: 0), capacities, Presets());

        Assert.Empty(comparison.Rows);
        Assert.Contains("5.6 Sol", comparison.UnavailableReason);
    }

    private static QuotaCycleAnalysisResult Result(long input, long output) =>
        QuotaCycleAnalysisResult.Empty(
            new CodexQuotaCycle(Start, Start.AddDays(7), Start.AddDays(7), 2, 5m, true), "") with
        {
            UsageSamples = new[] { new QuotaCycleUsageSample(Start, input, 0, 0, output,
                input + output, 1) }
        };

    private static QuotaModelCapacityReport Capacities() => new("Pro 20X", new[]
    {
        Estimate("gpt-5.6-sol", 2400m),
        Estimate("gpt-6-astra", 1400m),
        Estimate("gpt-6-sol", 1091m)
    });

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
