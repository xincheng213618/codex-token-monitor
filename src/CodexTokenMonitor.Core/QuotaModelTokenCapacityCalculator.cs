using System.Text.RegularExpressions;

namespace CodexTokenMonitor;

internal sealed record QuotaModelTokenCapacityRow(
    string ModelId,
    decimal FullQuotaCost,
    decimal CostPerMillionTokens,
    decimal FullQuotaMillionTokens,
    decimal MinimumMillionTokens,
    decimal MaximumMillionTokens)
{
    public bool IsShareExtrapolation { get; init; }
    public decimal AttributedQuotaDropPercent { get; init; }
}

internal enum QuotaModelTokenCapacityMissingReason
{
    MissingPrice,
    InsufficientCalibration,
    MissingTokenMix,
    ZeroTokenCost,
    OutOfRange
}

internal sealed record QuotaModelTokenCapacityMissingModel(
    string ModelId,
    QuotaModelTokenCapacityMissingReason Reason);

internal sealed record QuotaModelTokenCapacityReport(
    long InputTokens,
    long CachedInputTokens,
    long CacheWriteInputTokens,
    long OutputTokens,
    long TotalTokens,
    IReadOnlyList<QuotaModelTokenCapacityRow> Rows,
    IReadOnlyList<QuotaModelTokenCapacityMissingModel> UnestimatedModels,
    string UnavailableReason);

/// <summary>
/// Converts each model's calibrated full-quota cost to token capacity using the
/// same observed token mix. Models without calibration can use a separately
/// marked current-period quota-share extrapolation; it is never saved as calibration.
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
        var catalog = presets
            .Where(item => string.Equals(item.Provider, "OpenAI", StringComparison.OrdinalIgnoreCase) &&
                           item.CurrencySymbol == "$" && item.Schedule == PriceSchedule.Flat &&
                           item.Divisor > 0m && item.UncachedInput >= 0m && item.CachedInput >= 0m &&
                           item.CacheWriteInput is null or >= 0m && item.Output >= 0m &&
                           !CodexModelCost.IsPending(item))
            .GroupBy(item => ModelKey(string.IsNullOrWhiteSpace(item.ModelId) ? item.Model : item.ModelId),
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var calibratedModels = capacities.Estimates.Where(item => item.AverageFullQuotaCost > 0m)
            .Select(item => ModelKey(item.ModelId)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var failures = new Dictionary<string, QuotaModelTokenCapacityMissingReason>(StringComparer.OrdinalIgnoreCase);
        var hasTokenMix = usage.TotalTokens > 0 && (usage.InputTokens > 0 || usage.OutputTokens > 0);
        QuotaModelTokenCapacityReport Report(IReadOnlyList<QuotaModelTokenCapacityRow> rows, string reason)
        {
            var estimated = rows.Select(item => item.ModelId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return new(usage.InputTokens, usage.CachedInputTokens, usage.CacheWriteInputTokens,
                usage.OutputTokens, usage.TotalTokens, rows,
                observedModels.Where(item => !estimated.Contains(item)).Select(model =>
                    new QuotaModelTokenCapacityMissingModel(model,
                        !catalog.ContainsKey(model) ? QuotaModelTokenCapacityMissingReason.MissingPrice :
                        !hasTokenMix ? QuotaModelTokenCapacityMissingReason.MissingTokenMix :
                        failures.GetValueOrDefault(model, QuotaModelTokenCapacityMissingReason.InsufficientCalibration)))
                    .ToArray(), reason);
        }
        if (!hasTokenMix)
            return Report(Array.Empty<QuotaModelTokenCapacityRow>(), "本周期没有可用于估算的 Token 构成");

        var rows = new List<QuotaModelTokenCapacityRow>();
        void AddRow(string model, PricePreset preset, decimal fullCost,
            decimal minimumCost, decimal maximumCost, decimal attributedDrop = 0m)
        {
            // Use the same price basis that produced the calibrated capacity.
            var profile = CodexSubscriptionPricing.GetProfile(model, preset.ToProfile());
            var cost = usage.EstimateCost(profile);
            var millionTokens = usage.TotalTokens / 1_000_000m;
            if (millionTokens < 1m && cost > decimal.MaxValue * millionTokens)
            {
                failures[model] = QuotaModelTokenCapacityMissingReason.OutOfRange;
                return;
            }
            var costPerMillion = cost / millionTokens;
            if (costPerMillion <= 0m)
            {
                failures[model] = QuotaModelTokenCapacityMissingReason.ZeroTokenCost;
                return;
            }
            var lowerCost = minimumCost > 0m ? Math.Min(minimumCost, fullCost) : fullCost;
            var upperCost = Math.Max(maximumCost, fullCost);
            // Extremely small custom prices must not overflow into a false estimate.
            if (costPerMillion < 1m && upperCost > decimal.MaxValue * costPerMillion)
            {
                failures[model] = QuotaModelTokenCapacityMissingReason.OutOfRange;
                return;
            }
            rows.Add(new(model, fullCost, costPerMillion,
                fullCost / costPerMillion, lowerCost / costPerMillion, upperCost / costPerMillion)
            {
                IsShareExtrapolation = attributedDrop > 0m,
                AttributedQuotaDropPercent = attributedDrop
            });
        }
        foreach (var group in capacities.Estimates.Where(item => item.AverageFullQuotaCost > 0m)
                     .GroupBy(item => ModelKey(item.ModelId), StringComparer.OrdinalIgnoreCase))
        {
            if (!catalog.TryGetValue(group.Key, out var preset)) continue;
            var estimate = group.First();
            AddRow(group.Key, preset, estimate.AverageFullQuotaCost,
                estimate.MinimumFullQuotaCost, estimate.MaximumFullQuotaCost);
        }
        foreach (var group in result.Models.GroupBy(item => ModelKey(item.ModelId), StringComparer.OrdinalIgnoreCase))
        {
            if (calibratedModels.Contains(group.Key) || !catalog.TryGetValue(group.Key, out var preset) ||
                group.Any(item => !item.IsPriced)) continue;
            var attributedDrop = group.Sum(item => Math.Max(0m, item.QuotaDropPercent));
            var fullCost = QuotaMath.EstimateLimit(group.Sum(item => Math.Max(0m, item.EquivalentCost)), attributedDrop);
            if (fullCost is not > 0m) continue;
            // The attributed percentage is an analytical allocation in a mixed
            // period, not an independently measured model quota. Display only.
            AddRow(group.Key, preset, fullCost.Value, fullCost.Value, fullCost.Value, attributedDrop);
        }
        return Report(rows.OrderByDescending(item => item.FullQuotaMillionTokens).ToArray(),
            rows.Count == 0 ? "没有同时具备可用价格和同套餐满额估算的模型" : "");
    }

    internal static string ModelKey(string modelId) => Regex.Replace(
        CodexModelCost.NormalizeModelId(modelId), @"-\d{4}-\d{2}-\d{2}$", "");
}
