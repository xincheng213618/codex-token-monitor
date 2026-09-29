using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class RealPriceReferenceTests
{
    private static TokenUsageBucket Usage(string? model, long total = 1_000_000, string? tier = null)
    {
        var usage = new TokenUsageBucket();
        usage.Add(new TokenUsageEvent(DateTimeOffset.Now, total * 9 / 10, total * 8 / 10,
            total / 10, total / 20, total, Guid.NewGuid().ToString(), ModelId: model, ServiceTier: tier));
        return usage;
    }

    [Fact]
    public void Codex_PrefersActualPlanEvenWhenAnotherPlanCostsMore()
    {
        var estimate = RealPriceCalculator.Estimate(Usage("openai/GPT-5.6-Sol-2026-08-15"),
            UsageSource.Codex, "chatgpt_pro_20x");
        var line = Assert.Single(estimate.Lines);
        Assert.Equal("chatgpt_pro_20x", line.Reference!.PlanId);
        Assert.Equal(line.Reference.UsdPerMillion, estimate.KnownCost);
        Assert.False(line.UsedOtherPlan);
        Assert.True(estimate.IsComplete);
    }

    [Fact]
    public void Codex_MissingProModelSampleUsesDocumentedPlusReference()
    {
        var estimate = RealPriceCalculator.Estimate(Usage("gpt-6-sol"), UsageSource.Codex, "chatgpt_pro_20x");
        Assert.Equal("chatgpt_plus", Assert.Single(estimate.Lines).Reference!.PlanId);
        Assert.True(estimate.UsesOtherPlans);
        Assert.Contains("跨套餐参考", estimate.Describe());
        Assert.Equal(0.019083969m, estimate.KnownCost);
    }

    [Fact]
    public void ZCode_UsesHighestUnitPriceNotMaxTierAndKeepsOriginalCny()
    {
        var estimate = RealPriceCalculator.Estimate(Usage("GLM-5.3-Flash", 624_000_000), UsageSource.ZCode);
        Assert.Equal("glm_coding_lite_cn_new_peak", Assert.Single(estimate.Lines).Reference!.PlanId);
        Assert.InRange(estimate.KnownCost, 117.999999m, 118.000001m);
        Assert.Equal("¥", estimate.CurrencySymbol);
        Assert.Contains("套餐未匹配", estimate.Describe());
    }

    [Fact]
    public void ZCode_SelectedLiteUses118YuanOffPeakReferenceWithoutChangingApiRates()
    {
        var planId = RealPriceCalculator.ZCodeReferencePlanId("GLM Coding Lite");
        Assert.Equal("glm_coding_lite_cn_new_offpeak", planId);
        var estimate = RealPriceCalculator.Estimate(
            Usage("GLM-5.3-Flash", 1_249_000_000), UsageSource.ZCode, planId);

        var line = Assert.Single(estimate.Lines);
        Assert.Equal(planId, line.Reference!.PlanId);
        Assert.InRange(estimate.KnownCost, 117.999999m, 118.000001m);
        Assert.False(estimate.UsesOtherPlans);
        Assert.Contains("不是实付金额", estimate.Describe());
        Assert.Contains("非高峰", estimate.Describe());
        Assert.Null(RealPriceCalculator.ZCodeReferencePlanId("GLM Coding Pro"));
    }

    [Fact]
    public void UnknownModelsAndUnidentifiedUsageStayUnpricedInsteadOfLookingFree()
    {
        var usage = Usage("gpt-6-sol");
        usage.MergeFrom(Usage("not-in-table", 2_000_000));
        usage.MergeFrom(Usage(null, 3_000_000));
        var estimate = RealPriceCalculator.Estimate(usage, UsageSource.Codex);
        Assert.Equal(1_000_000, estimate.CoveredTokens);
        Assert.Equal(5_000_000, estimate.UnpricedTokens);
        Assert.Equal(2, estimate.UnpricedEvents);
        Assert.EndsWith(" *", estimate.Format());
        Assert.Contains("5,000,000", estimate.Describe());
        Assert.Equal("暂无数据", RealPriceCalculator.Estimate(Usage("not-in-table"), UsageSource.Codex).Format());
    }

    [Fact]
    public void ReferencesDoNotLeakAcrossSourcesOrMultiplyFastAndReasoningAgain()
    {
        Assert.Equal("暂无数据", RealPriceCalculator.Estimate(Usage("glm-5.3-flash"), UsageSource.Codex).Format());
        var fast = RealPriceCalculator.Estimate(Usage("gpt-6-sol", tier: "fast"), UsageSource.Codex);
        var standard = RealPriceCalculator.Estimate(Usage("gpt-6-sol", tier: "standard"), UsageSource.Codex);
        Assert.Equal(standard.KnownCost, fast.KnownCost);
        Assert.Equal(0.019083969m, fast.KnownCost);
    }

    [Theory]
    [InlineData("Pro 20X", "chatgpt_pro_20x")]
    [InlineData("ChatGPT Plus", "chatgpt_plus")]
    [InlineData("Pro 5x", "chatgpt_pro_5x")]
    [InlineData("Pro", null)]
    [InlineData("Business", null)]
    public void PlanMatchingDoesNotInventAnUnspecifiedTier(string label, string? expected)
    {
        Assert.Equal(expected, RealPriceCalculator.CodexPlanId(label));
    }
}
