using System.Text.RegularExpressions;

namespace CodexTokenMonitor;

internal sealed record QuotaModelTokenCapacityRow(
    string ModelId,
    decimal FullQuotaCost,
    decimal CostPerMillionTokens,
    decimal FullQuotaMillionTokens,
    decimal MinimumMillionTokens,
    decimal MaximumMillionTokens);

internal sealed record QuotaModelTokenCapacityReport(
    long InputTokens,
    long CachedInputTokens,
    long CacheWriteInputTokens,
    long OutputTokens,
    long TotalTokens,
    IReadOnlyList<QuotaModelTokenCapacityRow> Rows,
    IReadOnlyList<string> UnestimatedModels,
    string UnavailableReason);

/// <summary>
/// Converts each model's calibrated full-quota cost to token capacity using the
/// same observed token mix. No reference model or attributed quota share is needed.
/// </summary>
internal static class QuotaModelTokenCapacityCalculator
{
    public static QuotaModelTokenCapacityReport Calculate(
        QuotaCycleAnalysisResult result,
        QuotaModelCapacityReport capacities,
        IEnumerable<PricePreset> presets)
    {
        var usage = new TokenUsageBucket();
        foreach (var sample in result.UsageSamples)
        {
            usage.InputTokens = TokenCountMath.AddNonNegative(usage.InputTokens, sample.InputTokens);
            usage.CachedInputTokens = TokenCountMath.AddNonNegative(usage.CachedInputTokens, sample.CachedInputTokens);
            usage.CacheWriteInputTokens = TokenCountMath.AddNonNegative(
                usage.CacheWriteInputTokens, sample.CacheWriteInputTokens);
            usage.OutputTokens = TokenCountMath.AddNonNegative(usage.OutputTokens, sample.OutputTokens);
            usage.TotalTokens = TokenCountMath.AddNonNegative(usage.TotalTokens, sample.TotalTokens);
        }
        usage.NormalizeInPlace();

        var observedModels = result.Models.Select(item => ModelKey(item.ModelId))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        QuotaModelTokenCapacityReport Report(IReadOnlyList<QuotaModelTokenCapacityRow> rows, string reason)
        {
            var estimated = rows.Select(item => item.ModelId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return new(usage.InputTokens, usage.CachedInputTokens, usage.CacheWriteInputTokens,
                usage.OutputTokens, usage.TotalTokens, rows,
                observedModels.Where(item => !estimated.Contains(item)).ToArray(), reason);
        }
        if (usage.TotalTokens <= 0 || (usage.InputTokens == 0 && usage.OutputTokens == 0))
            return Report(Array.Empty<QuotaModelTokenCapacityRow>(), "本周期没有可用于估算的 Token 构成");

        var catalog = presets
            .Where(item => string.Equals(item.Provider, "OpenAI", StringComparison.OrdinalIgnoreCase) &&
                           item.CurrencySymbol == "$" && item.Schedule == PriceSchedule.Flat &&
                           item.Divisor > 0m && item.UncachedInput >= 0m && item.CachedInput >= 0m &&
                           item.CacheWriteInput is null or >= 0m && item.Output >= 0m &&
                           !CodexModelCost.IsPending(item))
            .GroupBy(item => ModelKey(string.IsNullOrWhiteSpace(item.ModelId) ? item.Model : item.ModelId),
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var rows = new List<QuotaModelTokenCapacityRow>();
        foreach (var group in capacities.Estimates.Where(item => item.AverageFullQuotaCost > 0m)
                     .GroupBy(item => ModelKey(item.ModelId), StringComparer.OrdinalIgnoreCase))
        {
            if (!catalog.TryGetValue(group.Key, out var preset)) continue;
            var estimate = group.First();
            // Use the same price basis that produced the calibrated capacity.
            var profile = CodexSubscriptionPricing.GetProfile(group.Key, preset.ToProfile());
            var cost = usage.EstimateCost(profile);
            var millionTokens = usage.TotalTokens / 1_000_000m;
            if (millionTokens < 1m && cost > decimal.MaxValue * millionTokens) continue;
            var costPerMillion = cost / millionTokens;
            if (costPerMillion <= 0m) continue;
            var lowerCost = estimate.MinimumFullQuotaCost > 0m
                ? Math.Min(estimate.MinimumFullQuotaCost, estimate.AverageFullQuotaCost)
                : estimate.AverageFullQuotaCost;
            var upperCost = Math.Max(estimate.MaximumFullQuotaCost, estimate.AverageFullQuotaCost);
            // Extremely small custom prices must not overflow into a false estimate.
            if (costPerMillion < 1m && upperCost > decimal.MaxValue * costPerMillion) continue;
            rows.Add(new(group.Key, estimate.AverageFullQuotaCost, costPerMillion,
                estimate.AverageFullQuotaCost / costPerMillion,
                lowerCost / costPerMillion, upperCost / costPerMillion));
        }
        return Report(rows.OrderByDescending(item => item.FullQuotaMillionTokens).ToArray(),
            rows.Count == 0 ? "没有同时具备可用价格和同套餐满额估算的模型" : "");
    }

    private static string ModelKey(string modelId) => Regex.Replace(
        CodexModelCost.NormalizeModelId(modelId), @"-\d{4}-\d{2}-\d{2}$", "");
}
