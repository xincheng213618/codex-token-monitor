using System.Text.RegularExpressions;

namespace CodexTokenMonitor;

internal sealed record ModelCostLine(string ModelId, TokenUsageBucket Usage, decimal? Cost);

internal sealed record ModelCostEstimate(decimal KnownCost, long UnpricedTokens, long UnpricedEvents,
    IReadOnlyList<ModelCostLine> Models)
{
    // Codex estimates are USD; source-group estimates take the currency of
    // their first priced preset (e.g. ¥ for the ZCode group).
    public string CurrencySymbol { get; init; } = "$";
    public decimal QuotaBaseCost { get; init; } = KnownCost;
    public decimal FastSurcharge { get; init; }
    public long FastEvents { get; init; }
    public long UnknownTierEvents { get; init; }
    public long UnknownFastRateEvents { get; init; }
    public decimal QuotaEquivalentCost => QuotaBaseCost > decimal.MaxValue - FastSurcharge ? decimal.MaxValue : QuotaBaseCost + FastSurcharge;
    public bool IsComplete => UnpricedTokens == 0 && UnpricedEvents == 0;
    public decimal? CompleteCost => IsComplete ? KnownCost : null;
    public long MissingModelEvents => Math.Max(0, UnpricedEvents - Models.Where(item => item.Cost is null).Sum(item => item.Usage.Events));
    private string MissingSummary => string.Join("、", new[]
    {
        MissingModelEvents > 0 ? "缺模型名" : null,
        Models.Any(item => item.Cost is null && CodexModelCost.HasNoPublicPrice(item.ModelId)) ? "Spark 未计价" : null,
        Models.Any(item => item.Cost is null && !CodexModelCost.HasNoPublicPrice(item.ModelId)) ? "含 0x 待填" : null
    }.Where(item => item is not null));
    public string Format(string format = "N2") => IsComplete
        ? $"{CurrencySymbol}{KnownCost.ToString(format, CultureInfo.InvariantCulture)}"
        : $"{CurrencySymbol}{KnownCost.ToString(format, CultureInfo.InvariantCulture)}（{MissingSummary}）";

    public string FormatQuotaCost(string format = "N2")
    {
        var details = new List<string>();
        if (!IsComplete) details.Add(MissingSummary);
        if (UnknownFastRateEvents > 0) details.Add("Fast 倍率待补");
        return $"{CurrencySymbol}{QuotaEquivalentCost.ToString(format, CultureInfo.InvariantCulture)}" +
            (details.Count > 0 ? $"（{string.Join("、", details)}）" : "");
    }
    public string SpeedDescription => $"Fast / priority {FastEvents:N0} 条，按 ChatGPT Fast 倍率增加参考费用 ${FastSurcharge:N2}。" +
        (UnknownTierEvents > 0 ? $" {UnknownTierEvents:N0} 条没有明确速度记录，默认按普速 1x 估算；Token 已计入，若另一台电脑有速度标记，同步可补全。" : "") +
        (UnknownFastRateEvents > 0 ? $" {UnknownFastRateEvents:N0} 条 Fast 记录尚无对应倍率，暂按普速基准估算。" : "") +
        " 此项用于额度消耗比较；API Priority 单价是另一种计费口径。\n" + CodexSubscriptionPricing.Description;

    public string MissingPriceDescription
    {
        get
        {
            var missing = Models.Where(item => item.Cost is null)
                .Select(item => $"{item.ModelId}（{item.Usage.Events:N0} 条；{(CodexModelCost.HasNoPublicPrice(item.ModelId) ? "暂无公开 API 单价" : "0x 待填价格")}）").ToList();
            var namedEvents = Models.Where(item => item.Cost is null).Sum(item => item.Usage.Events);
            if (UnpricedEvents > namedEvents) missing.Add($"缺模型名称（{UnpricedEvents - namedEvents:N0} 条；已有 Token，无法选择对应单价。若来自旧版同步数据，请在来源电脑更新程序、完成统计后同步全部历史）");
            return IsComplete ? "" : $"未计价：{UnpricedEvents:N0} 条 / {UnpricedTokens:N0} Token：{string.Join("、", missing)}";
        }
    }
}

internal static class CodexModelCost
{
    public const string ReserveModelId = "gpt-reserve";
    private const string LunaModelId = "gpt-5.6-luna";

    public static bool IsFastTier(string tier) => tier is "fast" or "priority";
    // ChatGPT quota credits, not API Priority billing. Current official rates:
    // https://learn.chatgpt.com/docs/agent-configuration/speed
    public static decimal? FastQuotaMultiplier(string modelId) => ModelKey(modelId) switch
    {
        "gpt-6-astra" or "gpt-5.6-sol" or "gpt-5.6-terra" or "gpt-5.6-luna" or "gpt-5.5" or ReserveModelId => 2.5m,
        "gpt-5.4" => 2m,
        _ => null
    };
    public const string PlaceholderPriceSource = "新模型默认 0x（待填写）；当前按 0 费用统计";
    public const string NoPublicPriceSource = "暂无公开 API 单价（研究预览，不代表免费）：https://learn.chatgpt.com/docs/pricing";
    public static bool HasNoPublicPrice(string modelId) => NormalizeModelId(modelId) == "gpt-5.3-codex-spark";
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PriceSettings, Dictionary<string, PricePreset>> Catalogs = new();

    private static Dictionary<string, PricePreset> BuildCatalog(IEnumerable<PricePreset> catalog, bool openAiOnly = true) => catalog
        .Where(p => !openAiOnly ||
                    (string.Equals(p.Provider, "OpenAI", StringComparison.OrdinalIgnoreCase) && p.CurrencySymbol == "$"))
        .GroupBy(p => NormalizeModelId(string.IsNullOrWhiteSpace(p.ModelId) ? p.Model : p.ModelId))
        .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    // The catalog is independent of which comparison cards are currently visible.
    // Explicit API ids are preferred; display names of existing presets remain compatible.
    public static ModelCostEstimate Estimate(TokenUsageBucket usage, IEnumerable<PricePreset>? catalog = null)
    {
        var prices = catalog is null
            ? Catalogs.GetValue(PriceSettingsStore.Current, settings => BuildCatalog(settings.CodexPresets))
            : BuildCatalog(catalog);
        return EstimateWith(prices, usage);
    }

    /// <summary>
    /// Estimates a source-group bucket against that group's own presets, so
    /// non-Codex sources (ZCode/DSH/...) price their actual model ids.
    /// </summary>
    public static ModelCostEstimate Estimate(TokenUsageBucket usage, string priceGroup)
    {
        var catalog = PriceSettingsStore.Current.PresetsForGroup(PricePresetGroups.Normalize(priceGroup));
        return EstimateWith(BuildCatalog(catalog, openAiOnly: false), usage);
    }

    private static ModelCostEstimate EstimateWith(Dictionary<string, PricePreset> prices, TokenUsageBucket usage)
    {
        long pricedTokens = 0, pricedEvents = 0;
        decimal cost = 0, quotaBaseCost = 0;
        decimal fastSurcharge = 0;
        long fastEvents = 0, knownTierEvents = 0, unknownFastRateEvents = 0;
        var models = new List<ModelCostLine>();
        string? currencySymbol = null;
        foreach (var (model, tokens) in usage.ModelUsage.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            TryFindPrice(prices, model, out var preset);
            var pending = IsPending(preset);
            if (!pending && currencySymbol is null) currencySymbol = preset!.CurrencySymbol;
            decimal? amount = pending ? null : tokens.EstimateCost(preset!.ToProfile());
            var quotaProfile = pending ? null : CodexSubscriptionPricing.GetProfile(model, preset!.ToProfile());
            foreach (var (tier, tierUsage) in tokens.ServiceTierUsage)
            {
                if (tier is "fast" or "priority" or "default" or "standard")
                    knownTierEvents = TokenCountMath.AddNonNegative(knownTierEvents, tierUsage.Events);
                if (!IsFastTier(tier)) continue;
                fastEvents = TokenCountMath.AddNonNegative(fastEvents, tierUsage.Events);
                if (FastQuotaMultiplier(model) is not { } factor)
                    unknownFastRateEvents = TokenCountMath.AddNonNegative(unknownFastRateEvents, tierUsage.Events);
                else if (amount is not null)
                {
                    var baseline = tierUsage.EstimateCost(quotaProfile!);
                    var extra = baseline > decimal.MaxValue / (factor - 1) ? decimal.MaxValue : baseline * (factor - 1);
                    fastSurcharge = fastSurcharge > decimal.MaxValue - extra ? decimal.MaxValue : fastSurcharge + extra;
                }
            }
            models.Add(new ModelCostLine(model, tokens, amount));
            if (amount is null) continue;
            cost = cost > decimal.MaxValue - amount.Value ? decimal.MaxValue : cost + amount.Value;
            var quotaAmount = tokens.EstimateCost(quotaProfile!);
            quotaBaseCost = quotaBaseCost > decimal.MaxValue - quotaAmount ? decimal.MaxValue : quotaBaseCost + quotaAmount;
            pricedTokens = TokenCountMath.AddNonNegative(pricedTokens, tokens.TotalTokens);
            pricedEvents = TokenCountMath.AddNonNegative(pricedEvents, tokens.Events);
        }
        return new ModelCostEstimate(cost, TokenCountMath.SubtractNonNegative(usage.TotalTokens, pricedTokens),
            TokenCountMath.SubtractNonNegative(usage.Events, pricedEvents), models)
        {
            CurrencySymbol = currencySymbol ?? "$",
            QuotaBaseCost = quotaBaseCost, FastSurcharge = fastSurcharge, FastEvents = fastEvents,
            UnknownTierEvents = TokenCountMath.SubtractNonNegative(usage.Events, knownTierEvents),
            UnknownFastRateEvents = unknownFastRateEvents
        };
    }

    public static string NormalizeModelId(string? model)
    {
        var value = (model ?? "").Trim().ToLowerInvariant();
        if (value.StartsWith("openai/", StringComparison.Ordinal)) value = value[7..];
        value = Regex.Replace(value, @"\s+", "-");
        return value == "gpt-5.6" ? "gpt-5.6-sol" : value;
    }

    public static bool IsReserveModel(string? modelId) => ModelKey(modelId) == ReserveModelId;

    public static TokenUsageBucket? FindModelUsage(TokenUsageBucket usage, string modelId)
    {
        ArgumentNullException.ThrowIfNull(usage);
        var key = ModelKey(modelId);
        var matches = usage.ModelUsage
            .Where(pair => ModelKey(pair.Key) == key)
            .Select(pair => pair.Value)
            .ToArray();
        if (matches.Length == 0)
        {
            return null;
        }

        if (matches.Length == 1)
        {
            return matches[0];
        }

        var aggregate = new TokenUsageBucket { StartLocal = usage.StartLocal };
        foreach (var match in matches)
        {
            aggregate.MergeFrom(match);
        }

        return aggregate;
    }

    public static string DefaultModelId(string provider, string model) =>
        string.Equals(provider, "OpenAI", StringComparison.OrdinalIgnoreCase) ? model switch
        {
            "GPT-5.5 Standard Short" => "gpt-5.5",
            "GPT-5.4 Standard Short" => "gpt-5.4",
            "GPT-5.4 mini Short" => "gpt-5.4-mini",
            "GPT-5.2 Reference" => "gpt-5.2",
            _ => ""
        } : "";

    public static string DescribeModels(TokenUsageBucket usage)
    {
        var models = usage.ModelUsage.OrderBy(m => m.Key, StringComparer.OrdinalIgnoreCase).Select(pair =>
        {
            var fast = pair.Value.ServiceTierUsage.Where(p => IsFastTier(p.Key)).Sum(p => p.Value.Events);
            return fast == 0 ? pair.Key : fast == pair.Value.Events ? $"{pair.Key} · Fast" : $"{pair.Key} · Fast {fast:N0} 条";
        }).ToList();
        var identified = usage.ModelUsage.Values.Aggregate(0L, (n, b) => TokenCountMath.AddNonNegative(n, b.Events));
        if (identified < usage.Events) models.Add("未识别");
        return models.Count == 0 ? "未识别" : string.Join(" / ", models);
    }

    public static decimal? EstimateQuotaValue(TokenUsageBucket usage, decimal usedPercent) =>
        QuotaMath.EstimateLimit(Estimate(usage).QuotaEquivalentCost, usedPercent);

    public static int AddMissingPresets(PriceSettings settings, IEnumerable<string> models)
    {
        var catalog = BuildCatalog(settings.CodexPresets);
        var count = 0;
        foreach (var model in models.Where(m => !string.IsNullOrWhiteSpace(m)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var key = NormalizeModelId(model);
            if (IsReserveModel(model) || catalog.ContainsKey(key) || catalog.ContainsKey(ModelKey(key))) continue;
            var preset = new PricePreset { Group = PricePresetGroups.Codex, Provider = "OpenAI",
                Model = model, ModelId = model, CurrencySymbol = "$", UnitLabel = "USD / 1M tokens",
                Divisor = 1_000_000m, UncachedInput = 0, CachedInput = 0, CacheWriteInput = 0, Output = 0,
                Source = HasNoPublicPrice(model) ? NoPublicPriceSource : PlaceholderPriceSource };
            settings.CodexPresets.Add(preset);
            catalog[key] = preset;
            count++;
        }
        return count;
    }

    private static string ModelKey(string? model) => Regex.Replace(NormalizeModelId(model), @"-\d{4}-\d{2}-\d{2}$", "");

    private static bool IsPending(PricePreset? preset) => preset is null ||
        (preset.Source == PlaceholderPriceSource || preset.Source == NoPublicPriceSource) &&
        preset.UncachedInput == 0 && preset.CachedInput == 0 && preset.Output == 0 && (preset.CacheWriteInput ?? 0) == 0;

    private static bool TryFindPrice(Dictionary<string, PricePreset> prices, string model, out PricePreset? preset)
    {
        var key = NormalizeModelId(model);
        if (!prices.TryGetValue(key, out preset))
        {
            prices.TryGetValue(ModelKey(key), out preset);
        }

        // Codex reports the post-limit fallback as gpt-reserve. It is not a
        // separately billed API model, so use Luna's four-part price. A
        // non-zero user-supplied reserve price still takes precedence.
        if (IsReserveModel(key) && IsPending(preset))
        {
            prices.TryGetValue(LunaModelId, out preset);
        }

        return preset is not null;
    }
}
