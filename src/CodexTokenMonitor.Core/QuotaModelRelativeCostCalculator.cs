using System.Text.RegularExpressions;

namespace CodexTokenMonitor;

internal sealed record QuotaModelRelativeCostRow(
    string ModelId,
    decimal FullQuotaCost,
    decimal PriceRatio,
    decimal CapacityRatio,
    decimal QuotaRatio,
    decimal InputQuotaRatio,
    decimal CachedInputQuotaRatio,
    decimal OutputQuotaRatio);

internal sealed record QuotaModelRelativeCostReport(
    string ReferenceModelId,
    long InputTokens,
    long CachedInputTokens,
    long CacheWriteInputTokens,
    long OutputTokens,
    IReadOnlyList<QuotaModelRelativeCostRow> Rows,
    string UnavailableReason);

/// <summary>
/// Compares the quota consumed by an identical standard, short-context token mix.
/// Full-quota costs are empirical subscription-equivalent estimates, not dollar balances.
/// </summary>
internal static class QuotaModelRelativeCostCalculator
{
    public const string DefaultReferenceModelId = "gpt-5.6-sol";

    public static QuotaModelRelativeCostReport Calculate(
        QuotaCycleAnalysisResult result,
        QuotaModelCapacityReport capacities,
        IEnumerable<PricePreset> presets,
        string referenceModelId = DefaultReferenceModelId)
    {
        var referenceKey = ModelKey(referenceModelId);
        QuotaModelRelativeCostReport Unavailable(string reason) =>
            new(referenceKey, 0, 0, 0, 0, Array.Empty<QuotaModelRelativeCostRow>(), reason);

        var estimates = capacities.Estimates
            .Where(item => item.AverageFullQuotaCost > 0m)
            .GroupBy(item => ModelKey(item.ModelId), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        if (!estimates.TryGetValue(referenceKey, out var reference))
        {
            return Unavailable("缺少 5.6 Sol 的同套餐满额估算");
        }

        var catalog = presets
            .Where(item => string.Equals(item.Provider, "OpenAI", StringComparison.OrdinalIgnoreCase) &&
                           item.CurrencySymbol == "$" && item.Schedule == PriceSchedule.Flat &&
                           item.Divisor > 0m && item.UncachedInput >= 0m &&
                           item.CachedInput >= 0m && item.CacheWriteInput is null or >= 0m &&
                           item.Output >= 0m &&
                           !CodexModelCost.IsPending(item))
            .GroupBy(item => ModelKey(string.IsNullOrWhiteSpace(item.ModelId) ? item.Model : item.ModelId),
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        if (!catalog.TryGetValue(referenceKey, out var referencePreset))
        {
            return Unavailable("缺少 5.6 Sol 的可用价格预设");
        }

        var usage = new TokenUsageBucket();
        foreach (var sample in result.UsageSamples)
        {
            usage.InputTokens = TokenCountMath.AddNonNegative(usage.InputTokens, sample.InputTokens);
            usage.CachedInputTokens = TokenCountMath.AddNonNegative(usage.CachedInputTokens, sample.CachedInputTokens);
            usage.CacheWriteInputTokens = TokenCountMath.AddNonNegative(
                usage.CacheWriteInputTokens, sample.CacheWriteInputTokens);
            usage.OutputTokens = TokenCountMath.AddNonNegative(usage.OutputTokens, sample.OutputTokens);
        }
        usage.NormalizeInPlace();
        if (usage.InputTokens == 0 && usage.OutputTokens == 0)
        {
            return Unavailable("本周期没有可用于统一比较的 Token 构成");
        }

        var referenceProfile = CodexSubscriptionPricing.GetProfile(referenceKey, referencePreset.ToProfile());
        var referencePrice = usage.EstimateCost(referenceProfile);
        if (referencePrice <= 0m)
        {
            return Unavailable("5.6 Sol 的统一 Token 价格为零");
        }

        var rows = new List<QuotaModelRelativeCostRow>();
        foreach (var (modelId, estimate) in estimates)
        {
            if (!catalog.TryGetValue(modelId, out var preset)) continue;
            var profile = CodexSubscriptionPricing.GetProfile(modelId, preset.ToProfile());
            var price = usage.EstimateCost(profile);
            if (price <= 0m) continue;

            var priceRatio = price / referencePrice;
            var capacityRatio = reference.AverageFullQuotaCost / estimate.AverageFullQuotaCost;
            var inputRatio = RateRatio(profile.UncachedInputPerMillion,
                referenceProfile.UncachedInputPerMillion, capacityRatio);
            var cachedRatio = RateRatio(profile.CachedInputPerMillion,
                referenceProfile.CachedInputPerMillion, capacityRatio);
            var outputRatio = RateRatio(profile.OutputPerMillion,
                referenceProfile.OutputPerMillion, capacityRatio);
            rows.Add(new QuotaModelRelativeCostRow(modelId, estimate.AverageFullQuotaCost,
                priceRatio, capacityRatio, priceRatio * capacityRatio,
                inputRatio, cachedRatio, outputRatio));
        }

        return new QuotaModelRelativeCostReport(referenceKey, usage.InputTokens, usage.CachedInputTokens,
            usage.CacheWriteInputTokens, usage.OutputTokens,
            rows.OrderByDescending(item => item.QuotaRatio).ToArray(),
            rows.Count == 0 ? "没有同时具备价格和满额估算的模型" : "");
    }

    private static decimal RateRatio(decimal modelRate, decimal referenceRate, decimal capacityRatio) =>
        referenceRate > 0m ? modelRate / referenceRate * capacityRatio : 0m;

    private static string ModelKey(string modelId) => Regex.Replace(
        CodexModelCost.NormalizeModelId(modelId), @"-\d{4}-\d{2}-\d{2}$", "");
}
