using System.Text;
using System.Text.RegularExpressions;

namespace CodexTokenMonitor;

internal sealed record RealPriceReference
{
    public string Id { get; init; } = "";
    public string Source { get; init; } = "";
    public string PlanId { get; init; } = "";
    public string Plan { get; init; } = "";
    public string Model { get; init; } = "";
    public string Currency { get; init; } = "";
    public decimal MonthlyFee { get; init; }
    public long MonthlyTokens { get; init; }
    public decimal UsdPerMillion { get; init; }
    public string Workload { get; init; } = "";
    public string Confidence { get; init; } = "";
    public string Evidence { get; init; } = "";
    public string Note { get; init; } = "";

    // Keep USD rows as published. For CNY rows use the original fee and quota,
    // avoiding a round trip through the dataset's rounded USD exchange value.
    public decimal PricePerMillion => Currency == "CNY"
        ? MonthlyFee * 1_000_000m / MonthlyTokens : UsdPerMillion;
}

internal sealed class RealPriceCatalog
{
    private static readonly Lazy<RealPriceCatalog> Bundled = new(() =>
    {
        using var stream = typeof(RealPriceCatalog).Assembly.GetManifestResourceStream(
            "CodexTokenMonitor.Data.real-api-pricing.json")
            ?? throw new InvalidDataException("缺少真实价格参考数据。");
        var catalog = JsonSerializer.Deserialize<RealPriceCatalog>(stream)
            ?? throw new InvalidDataException("真实价格参考数据为空。");
        if (catalog.Points.Count == 0 || catalog.Points.Any(p => p.MonthlyTokens <= 0 ||
            p.UsdPerMillion <= 0 || p.MonthlyFee <= 0 ||
            (p.Source == "Codex" ? p.Currency != "USD" : p.Source != "ZCode" || p.Currency != "CNY")))
            throw new InvalidDataException("真实价格参考数据不完整。");
        return catalog;
    });

    public string SnapshotDate { get; init; } = "";
    public string Commit { get; init; } = "";
    public string SourceUrl { get; init; } = "";
    public List<RealPriceReference> Points { get; init; } = new();
    public static RealPriceCatalog Current => Bundled.Value;
}

internal sealed record RealPriceLine(string Model, long Tokens, RealPriceReference? Reference, decimal? Cost,
    bool UsedOtherPlan);

internal sealed record RealPriceEstimate(UsageSource Source, decimal KnownCost, long CoveredTokens,
    long UnpricedTokens, long UnpricedEvents, IReadOnlyList<RealPriceLine> Lines)
{
    public bool IsComplete => UnpricedTokens == 0 && UnpricedEvents == 0;
    public bool UsesOtherPlans => Lines.Any(line => line.UsedOtherPlan);
    public string CurrencySymbol => Source == UsageSource.ZCode ? "¥" : "$";
    public string Format() => CoveredTokens == 0 && (UnpricedTokens > 0 || UnpricedEvents > 0)
        ? "暂无数据"
        : $"{CurrencySymbol}{KnownCost.ToString("N4", CultureInfo.InvariantCulture)}{(IsComplete ? "" : " *")}";

    public string Describe()
    {
        var catalog = RealPriceCatalog.Current;
        var text = new StringBuilder();
        text.AppendLine(Source == UsageSource.ZCode
            ? "免费 ZCode 用量：逐模型取网站 GLM Coding Plan 表中的最高真实单价折算；不是实付金额。"
            : "按当前 Codex 套餐优先匹配网站参考价；无对应样本时借用同模型最高参考价，并逐项标明。");
        text.AppendLine("公式：各模型全口径 Total Token ÷ 1,000,000 × 参考单价，再求和。");
        text.AppendLine("综合单价沿用来源负载假设，不等同于逐项 API 计费；不追加 Fast 倍率，不计额外重置优惠。");
        text.AppendLine($"数据快照：{catalog.SnapshotDate} · 固定参考快照，历史区间也按此快照折算。");
        foreach (var line in Lines)
        {
            if (line.Reference is not { } reference)
            {
                text.AppendLine($"{line.Model}：{line.Tokens:N0} token · 无对应参考价");
                continue;
            }
            text.AppendLine($"{line.Model}：{CurrencySymbol}{reference.PricePerMillion:N6}/MTok × {line.Tokens:N0} token = {CurrencySymbol}{line.Cost:N4}");
            text.AppendLine($"  {reference.Plan}{(line.UsedOtherPlan ? "（跨套餐参考）" : "")} · 置信度 {reference.Confidence} · {(reference.Workload == "measured" ? "实测负载" : "来源标准负载")}");
        }
        if (!IsComplete)
            text.AppendLine($"* 未覆盖 {UnpricedTokens:N0} token / {UnpricedEvents:N0} 条记录；金额只包含已匹配部分，未知不按免费处理。");
        text.AppendLine(catalog.SourceUrl);
        return text.ToString().TrimEnd();
    }
}

internal static class RealPriceCalculator
{
    public static bool Supports(UsageSource source) => source is UsageSource.Codex or UsageSource.ZCode;

    public static string? CodexPlanId(string? planName)
    {
        var name = (planName ?? "").Trim().ToLowerInvariant().Replace(" ", "");
        return name switch
        {
            "plus" or "chatgptplus" => "chatgpt_plus",
            "pro20x" or "chatgptpro20x" => "chatgpt_pro_20x",
            "pro5x" or "chatgptpro5x" => "chatgpt_pro_5x",
            _ => null
        };
    }

    public static RealPriceEstimate Estimate(TokenUsageBucket usage, UsageSource source,
        string? preferredPlanId = null, IEnumerable<RealPriceReference>? references = null)
    {
        var candidates = (references ?? RealPriceCatalog.Current.Points)
            .Where(p => Supports(source) && p.Source == source.ToString()).ToArray();
        var lines = new List<RealPriceLine>();
        decimal amount = 0;
        long coveredTokens = 0, coveredEvents = 0;
        foreach (var (model, bucket) in usage.ModelUsage.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            var matches = candidates.Where(p => ModelKey(p.Model) == ModelKey(model)).ToArray();
            var exact = source == UsageSource.Codex && preferredPlanId is not null
                ? matches.FirstOrDefault(p => p.PlanId == preferredPlanId) : null;
            var reference = exact ?? matches.OrderByDescending(p => p.PricePerMillion)
                .ThenBy(p => p.Id, StringComparer.Ordinal).FirstOrDefault();
            decimal? cost = reference is null ? null : bucket.TotalTokens / 1_000_000m * reference.PricePerMillion;
            lines.Add(new(model, bucket.TotalTokens, reference, cost,
                source == UsageSource.Codex && reference is not null && preferredPlanId is not null && exact is null));
            if (cost is null) continue;
            amount += cost.Value;
            coveredTokens = TokenCountMath.AddNonNegative(coveredTokens, bucket.TotalTokens);
            coveredEvents = TokenCountMath.AddNonNegative(coveredEvents, bucket.Events);
        }
        return new(source, amount, coveredTokens,
            TokenCountMath.SubtractNonNegative(usage.TotalTokens, coveredTokens),
            TokenCountMath.SubtractNonNegative(usage.Events, coveredEvents), lines);
    }

    private static string ModelKey(string model) => Regex.Replace(CodexModelCost.NormalizeModelId(model), @"-\d{4}-\d{2}-\d{2}$", "");
}
