namespace CodexTokenMonitor;

internal sealed record ModelUsageBreakdownCost(decimal Amount, string CurrencySymbol, string UnitLabel, double? Share);

internal sealed record ModelUsageBreakdownRow(string ModelId, long Tokens, double TokenShare,
    IReadOnlyList<ModelUsageBreakdownCost> Costs, long UnpricedTokens, long UnpricedEvents);

internal sealed record ModelUsageBreakdown(long TotalTokens, long UnpricedTokens, long UnpricedEvents,
    IReadOnlyList<ModelCostTotal> CostTotals, IReadOnlyList<ModelUsageBreakdownRow> Rows)
{
    public bool HasUnpricedUsage => UnpricedTokens > 0 || UnpricedEvents > 0;
    public bool HasMixedCostUnits => CostTotals.Count > 1;

    public static ModelUsageBreakdown Build(TokenUsageBucket usage, ModelCostEstimate estimate)
    {
        var totalTokens = TokenCountMath.NonNegative(usage.TotalTokens);
        var identifiedTokens = SumTokens(estimate.Models);
        var identifiedEvents = estimate.Models.Aggregate(0L,
            (total, line) => TokenCountMath.AddNonNegative(total, line.Usage.Events));
        var missingTokens = (long)Math.Max(0, totalTokens - identifiedTokens);
        var missingEvents = TokenCountMath.SubtractNonNegative(usage.Events, identifiedEvents);

        // The persisted totals saturate their scalar types. Keep the ratios
        // normalized even when the individual model totals sum above that limit.
        var tokenShareBase = Math.Max(totalTokens, identifiedTokens);
        var costShareBase = estimate.HasMixedUnits ? 0d : estimate.Models.Sum(line => (double)(line.Cost ?? 0m));
        var rows = new List<ModelUsageBreakdownRow>();
        foreach (var group in estimate.Models.GroupBy(line => CodexModelCost.ModelKey(line.ModelId)))
        {
            var tokens = SumTokens(group);
            var unpriced = group.Where(line => line.Cost is null).ToArray();
            var costs = new List<ModelUsageBreakdownCost>();
            foreach (var total in estimate.CostTotals)
            {
                var priced = group.Where(line => line.Cost.HasValue &&
                    string.Equals(line.CurrencySymbol, total.CurrencySymbol, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(line.UnitLabel, total.UnitLabel, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (priced.Length == 0) continue;

                var amount = priced.Aggregate(0m, (sum, line) => CodexModelCost.AddCosts(sum, line.Cost!.Value));
                var share = costShareBase > 0 ? priced.Sum(line => (double)line.Cost!.Value) / costShareBase : (double?)null;
                costs.Add(new ModelUsageBreakdownCost(amount, total.CurrencySymbol, total.UnitLabel, share));
            }

            rows.Add(new ModelUsageBreakdownRow(group.Key, ClampTokens(tokens), Share(tokens, tokenShareBase), costs,
                ClampTokens(SumTokens(unpriced)), unpriced.Aggregate(0L,
                    (sum, line) => TokenCountMath.AddNonNegative(sum, line.Usage.Events))));
        }

        if (missingTokens > 0 || missingEvents > 0)
        {
            rows.Add(new ModelUsageBreakdownRow("", missingTokens, Share(missingTokens, tokenShareBase), [],
                missingTokens, missingEvents));
        }

        return new ModelUsageBreakdown(totalTokens, estimate.UnpricedTokens, estimate.UnpricedEvents, estimate.CostTotals,
            rows.OrderByDescending(row => row.Tokens).ThenBy(row => row.ModelId, StringComparer.Ordinal).ToArray());
    }

    private static decimal SumTokens(IEnumerable<ModelCostLine> lines) =>
        lines.Sum(line => (decimal)TokenCountMath.NonNegative(line.Usage.TotalTokens));

    private static long ClampTokens(decimal tokens) => (long)Math.Min(tokens, long.MaxValue);

    private static double Share(decimal part, decimal total) => total > 0 ? (double)(part / total) : 0;
}
