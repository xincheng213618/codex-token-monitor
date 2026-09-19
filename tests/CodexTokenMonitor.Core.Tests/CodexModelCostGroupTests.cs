using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class CodexModelCostGroupTests
{
    private static TokenUsageBucket Bucket(string modelId, long total)
    {
        var bucket = new TokenUsageBucket();
        bucket.Add(new TokenUsageEvent(
            DateTimeOffset.Now, InputTokens: total, CachedInputTokens: 0, OutputTokens: 0,
            ReasoningOutputTokens: 0, TotalTokens: total, Key: $"group-{modelId}-{total}", ModelId: modelId));
        return bucket;
    }

    [Fact]
    public void GroupEstimate_PricesGlm53FlashWithYuanRates()
    {
        // 1M uncached input + 1M output at the GLM-5.3 Flash reference prices:
        // 0.80 + 2.80 = 3.60 yuan. Cached reads would use 0.23.
        // 1M uncached input and 1M output attributed to the model, mirroring
        // how event rows aggregate into a summary bucket's ModelUsage.
        var bucket = new TokenUsageBucket();
        bucket.Add(new TokenUsageEvent(
            DateTimeOffset.Now, InputTokens: 1_000_000, CachedInputTokens: 0, OutputTokens: 1_000_000,
            ReasoningOutputTokens: 0, TotalTokens: 2_000_000, Key: "glm-flash-full", ModelId: "GLM-5.3-Flash"));
        var cost = CodexModelCost.Estimate(bucket, PricePresetGroups.ZCode);

        Assert.True(cost.IsComplete);
        Assert.Equal("¥", cost.CurrencySymbol);
        Assert.Equal(3.60m, cost.KnownCost);
        Assert.Contains("¥3.60", cost.Format());
    }

    [Fact]
    public void GroupEstimate_MatchesCaseInsensitively()
    {
        var lower = CodexModelCost.Estimate(Bucket("glm-5.3-flash", 1_000_000), PricePresetGroups.ZCode);
        var upper = CodexModelCost.Estimate(Bucket("GLM-5.3-Flash", 1_000_000), PricePresetGroups.ZCode);

        Assert.Equal(lower.KnownCost, upper.KnownCost);
        Assert.Equal(lower.CurrencySymbol, upper.CurrencySymbol);
    }

    [Fact]
    public void GroupEstimate_MatchesActualGlm52IdToTheOneMillionPreset()
    {
        var cost = CodexModelCost.Estimate(Bucket("GLM-5.2", 1_000_000), PricePresetGroups.ZCode);

        Assert.True(cost.IsComplete);
        Assert.Equal("¥", cost.CurrencySymbol);
        Assert.Equal(8.00m, cost.KnownCost);
    }

    [Fact]
    public void GroupEstimate_KeepsYuanAndCreditsAsSeparateTotals()
    {
        var bucket = new TokenUsageBucket();
        bucket.Add(new TokenUsageEvent(
            DateTimeOffset.Now, InputTokens: 1_000_000, CachedInputTokens: 0, OutputTokens: 0,
            ReasoningOutputTokens: 0, TotalTokens: 1_000_000, Key: "glm", ModelId: "GLM-5.3-Flash"));
        bucket.Add(new TokenUsageEvent(
            DateTimeOffset.Now.AddSeconds(1), InputTokens: 1, CachedInputTokens: 0, OutputTokens: 0,
            ReasoningOutputTokens: 0, TotalTokens: 1, Key: "mimo", ModelId: "mimo-v2.5-pro"));

        var cost = CodexModelCost.Estimate(bucket, PricePresetGroups.ZCode);

        Assert.True(cost.IsComplete);
        Assert.True(cost.HasMixedUnits);
        Assert.Equal(2, cost.CostTotals.Count);
        Assert.Equal(0m, cost.KnownCost);
        Assert.Null(cost.CompleteCost);
        Assert.Contains("¥0.80", cost.Format());
        Assert.Contains("300.00 Credits", cost.Format());
        Assert.DoesNotContain("¥300.80", cost.Format());
    }

    [Fact]
    public void GroupEstimate_UnknownModel_ReportsUnpriced()
    {
        var cost = CodexModelCost.Estimate(Bucket("totally-unknown-model", 1_000_000), PricePresetGroups.ZCode);

        Assert.False(cost.IsComplete);
        Assert.Equal(1_000_000, cost.UnpricedTokens);
    }

    [Fact]
    public void CodexEstimate_KeepsDollarSymbol()
    {
        // The OpenAI-only catalog path must keep its USD formatting.
        var cost = CodexModelCost.Estimate(Bucket("totally-unknown-model", 1_000_000));

        Assert.Equal("$", cost.CurrencySymbol);
    }
}
