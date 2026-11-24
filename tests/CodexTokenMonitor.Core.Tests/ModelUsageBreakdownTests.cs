using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class ModelUsageBreakdownTests
{
    private static readonly DateTimeOffset Day = new(2026, 11, 18, 0, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public void Build_CombinesModelAliasesAndUsesTheSamePricesAsTheMainTotal()
    {
        var usage = new TokenUsageBucket();
        Add(usage, "GPT-6.2 Astra", 1_000_000);
        Add(usage, "openai/GPT-6.2-Sol-2026-11-17", 3_000_000);
        Add(usage, "gpt-6.2-sol", 1_000_000);

        var result = BuildCodexBreakdown(usage);

        Assert.Equal(5_000_000, result.TotalTokens);
        Assert.Equal(16m, Assert.Single(result.CostTotals).Amount);
        Assert.Collection(result.Rows, sol =>
        {
            Assert.Equal("gpt-6.2-sol", sol.ModelId);
            Assert.Equal(4_000_000, sol.Tokens);
            Assert.Equal(.8, sol.TokenShare);
            var cost = Assert.Single(sol.Costs);
            Assert.Equal(8m, cost.Amount);
            Assert.Equal(.5, cost.Share);
        }, astra =>
        {
            Assert.Equal("gpt-6.2-astra", astra.ModelId);
            Assert.Equal(.2, astra.TokenShare);
            var cost = Assert.Single(astra.Costs);
            Assert.Equal(8m, cost.Amount);
            Assert.Equal(.5, cost.Share);
        });
    }

    [Fact]
    public void Build_PreservesUnknownAndUnpricedUsageInTheTokenDenominator()
    {
        var usage = new TokenUsageBucket();
        Add(usage, "gpt-6.2-sol", 1_000_000);
        Add(usage, "future-model", 200_000);
        Add(usage, null, 300_000);

        var result = BuildCodexBreakdown(usage);

        Assert.True(result.HasUnpricedUsage);
        Assert.Equal(500_000, result.UnpricedTokens);
        Assert.Equal(2, result.UnpricedEvents);
        Assert.Equal(1, result.Rows.Sum(row => row.TokenShare), 10);
        var unknown = Assert.Single(result.Rows, row => row.ModelId == "");
        Assert.Equal(300_000, unknown.Tokens);
        Assert.Equal(.2, unknown.TokenShare);
        Assert.Empty(unknown.Costs);
        var pending = Assert.Single(result.Rows, row => row.ModelId == "future-model");
        Assert.Equal(pending.Tokens, pending.UnpricedTokens);
        Assert.Empty(pending.Costs);
        var priced = Assert.Single(result.Rows, row => row.ModelId == "gpt-6.2-sol");
        Assert.Equal(1d, Assert.Single(priced.Costs).Share);
    }

    [Fact]
    public void Build_TracksDifferentCostUnitsWithoutComparingTheirShares()
    {
        using var scope = MonitorCachePaths.PushLocalAppDataRoot(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        var usage = new TokenUsageBucket();
        Add(usage, "glm-5.3-flash", 1_000_000);
        Add(usage, "mimo-v2.5-pro", 1);

        var result = ModelUsageBreakdown.Build(usage, CodexModelCost.Estimate(usage, PricePresetGroups.ZCode));

        Assert.True(result.HasMixedCostUnits);
        Assert.False(result.HasUnpricedUsage);
        Assert.Equal(2, result.CostTotals.Count);
        Assert.Collection(result.Rows, glm =>
        {
            var cost = Assert.Single(glm.Costs);
            Assert.Equal("¥", cost.CurrencySymbol);
            Assert.Equal(.8m, cost.Amount);
            Assert.Null(cost.Share);
        }, mimo =>
        {
            var cost = Assert.Single(mimo.Costs);
            Assert.Equal("Credits", cost.CurrencySymbol);
            Assert.Equal(300m, cost.Amount);
            Assert.Null(cost.Share);
        });
    }

    [Fact]
    public void Build_EmptyUsageHasNoRowsOrHeadlineCosts()
    {
        var result = BuildCodexBreakdown(new TokenUsageBucket());

        Assert.Empty(result.Rows);
        Assert.Empty(result.CostTotals);
        Assert.Equal(0, result.TotalTokens);
        Assert.False(result.HasUnpricedUsage);
        Assert.False(result.HasMixedCostUnits);
    }

    private static ModelUsageBreakdown BuildCodexBreakdown(TokenUsageBucket usage) =>
        ModelUsageBreakdown.Build(usage, CodexModelCost.Estimate(usage, PricePreset.DefaultsForGroup(PricePresetGroups.Codex)));

    private static void Add(TokenUsageBucket usage, string? model, long tokens) =>
        usage.Add(new TokenUsageEvent(Day, tokens, 0, 0, 0, tokens, ModelId: model));
}
