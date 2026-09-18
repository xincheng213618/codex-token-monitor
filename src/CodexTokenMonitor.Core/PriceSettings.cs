using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace CodexTokenMonitor;

internal static class PricePresetGroups
{
    public const string Codex = "Codex";
    public const string ClaudeCode = "Claude Code";
    public const string ZCode = "ZCode";
    public const string WorkBuddy = "WorkBuddy";
    public const string Dsh = "DSH";

    public static IReadOnlyList<string> All => UsageSourceRegistry.PriceGroups;

    public static string ForSource(UsageSource source)
    {
        return UsageSourceRegistry.For(source).PriceGroup;
    }

    public static string Normalize(string group)
    {
        return UsageSourceRegistry.ForPriceGroup(group).PriceGroup;
    }
}

internal sealed class PriceSettings
{
    public int DisplayOrderVersion { get; set; } = 18;
    public string GptName { get; set; } = "GPT-5.6 Sol";
    public decimal GptUncachedInputPerMillion { get; set; } = 4.00m;
    public decimal GptCachedInputPerMillion { get; set; } = 0.40m;
    public decimal? GptCacheWriteInputPerMillion { get; set; } = 5.00m;
    public decimal GptOutputPerMillion { get; set; } = 20.00m;

    public decimal DeepSeekUncachedInputPerMillion { get; set; } = 1.00m;
    public decimal DeepSeekCachedInputPerMillion { get; set; } = 0.02m;
    public decimal DeepSeekOutputPerMillion { get; set; } = 4.00m;

    public decimal XiaomiUncachedInputCreditsPerToken { get; set; } = 300.00m;
    public decimal XiaomiCachedInputCreditsPerToken { get; set; } = 2.50m;
    public decimal XiaomiOutputCreditsPerToken { get; set; } = 600.00m;
    public List<PricePreset> Presets { get; set; } = new();
    public List<PricePreset> CodexPresets { get; set; } = PricePreset.DefaultsForGroup(PricePresetGroups.Codex).ToList();
    public List<PricePreset> ClaudeCodePresets { get; set; } = PricePreset.DefaultsForGroup(PricePresetGroups.ClaudeCode).ToList();
    public List<PricePreset> ZCodePresets { get; set; } = PricePreset.DefaultsForGroup(PricePresetGroups.ZCode).ToList();
    public List<PricePreset> WorkBuddyPresets { get; set; } = PricePreset.DefaultsForGroup(PricePresetGroups.WorkBuddy).ToList();
    public List<PricePreset> DshPresets { get; set; } = PricePreset.DefaultsForGroup(PricePresetGroups.Dsh).ToList();

    public PriceProfile ToGptProfile()
    {
        return new PriceProfile(
            string.IsNullOrWhiteSpace(GptName) ? "GPT-5.6 Sol" : GptName.Trim(),
            "$",
            GptUncachedInputPerMillion,
            GptCachedInputPerMillion,
            GptOutputPerMillion,
            1_000_000m,
            CacheWriteInputPerMillion: GptCacheWriteInputPerMillion);
    }

    public PriceProfile ToDeepSeekProfile()
    {
        return new PriceProfile(
            "DeepSeek V4.1 Flash",
            "¥",
            DeepSeekUncachedInputPerMillion,
            DeepSeekCachedInputPerMillion,
            DeepSeekOutputPerMillion,
            1_000_000m,
            PriceSchedule.DeepSeekBeijingPeakDouble);
    }

    public PriceProfile ToXiaomiProfile()
    {
        return new PriceProfile(
            "Xiaomi MiMo V2.5 Pro",
            "Credits",
            XiaomiUncachedInputCreditsPerToken,
            XiaomiCachedInputCreditsPerToken,
            XiaomiOutputCreditsPerToken,
            1m);
    }

    public PriceSettings Clone()
    {
        return new PriceSettings
        {
            DisplayOrderVersion = DisplayOrderVersion,
            GptName = GptName,
            GptUncachedInputPerMillion = GptUncachedInputPerMillion,
            GptCachedInputPerMillion = GptCachedInputPerMillion,
            GptCacheWriteInputPerMillion = GptCacheWriteInputPerMillion,
            GptOutputPerMillion = GptOutputPerMillion,
            DeepSeekUncachedInputPerMillion = DeepSeekUncachedInputPerMillion,
            DeepSeekCachedInputPerMillion = DeepSeekCachedInputPerMillion,
            DeepSeekOutputPerMillion = DeepSeekOutputPerMillion,
            XiaomiUncachedInputCreditsPerToken = XiaomiUncachedInputCreditsPerToken,
            XiaomiCachedInputCreditsPerToken = XiaomiCachedInputCreditsPerToken,
            XiaomiOutputCreditsPerToken = XiaomiOutputCreditsPerToken,
            Presets = Presets.Select(item => item.Clone()).ToList(),
            CodexPresets = CodexPresets.Select(item => item.Clone()).ToList(),
            ClaudeCodePresets = ClaudeCodePresets.Select(item => item.Clone()).ToList(),
            ZCodePresets = ZCodePresets.Select(item => item.Clone()).ToList(),
            WorkBuddyPresets = WorkBuddyPresets.Select(item => item.Clone()).ToList(),
            DshPresets = DshPresets.Select(item => item.Clone()).ToList()
        };
    }

    public List<PricePreset> PresetsForGroup(string group)
    {
        return PricePresetGroups.Normalize(group) switch
        {
            PricePresetGroups.ClaudeCode => ClaudeCodePresets,
            PricePresetGroups.ZCode => ZCodePresets,
            PricePresetGroups.WorkBuddy => WorkBuddyPresets,
            PricePresetGroups.Dsh => DshPresets,
            _ => CodexPresets
        };
    }

    public void SetPresetsForGroup(string group, List<PricePreset> presets)
    {
        switch (PricePresetGroups.Normalize(group))
        {
            case PricePresetGroups.ClaudeCode:
                ClaudeCodePresets = presets;
                break;
            case PricePresetGroups.ZCode:
                ZCodePresets = presets;
                break;
            case PricePresetGroups.WorkBuddy:
                WorkBuddyPresets = presets;
                break;
            case PricePresetGroups.Dsh:
                DshPresets = presets;
                break;
            default:
                CodexPresets = presets;
                break;
        }
    }
}

internal sealed class PricePreset
{
    public const string OpenAiPriceSource = "OpenAI 标准价（2026-09-05）：https://developers.openai.com/api/docs/pricing";
    public const string DeepSeekPriceSource = "DeepSeek API 官方定价（2026-09-10；空闲价；北京时间工作日高峰 ×2）：https://api-docs.deepseek.com/zh-cn/quick_start/pricing";
    public string Group { get; set; } = "";
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public string ModelId { get; set; } = "";
    public string CurrencySymbol { get; set; } = "$";
    public string UnitLabel { get; set; } = "1M tokens";
    public decimal Divisor { get; set; } = 1_000_000m;
    public decimal UncachedInput { get; set; }
    public decimal CachedInput { get; set; }
    public decimal? CacheWriteInput { get; set; }
    public decimal Output { get; set; }
    public string Source { get; set; } = "";
    public PriceSchedule Schedule { get; set; }

    public string DisplayName => string.IsNullOrWhiteSpace(Provider) ? Model : $"{Provider} {Model}".Trim();
    public string ScheduleLabel => Schedule == PriceSchedule.DeepSeekBeijingPeakDouble ? "峰谷自动" : "固定价";
    public decimal EffectiveCacheWriteInput => CacheWriteInput ?? UncachedInput;

    public PriceProfile ToProfile()
    {
        return new PriceProfile(
            DisplayName,
            string.IsNullOrWhiteSpace(CurrencySymbol) ? "$" : CurrencySymbol,
            UncachedInput,
            CachedInput,
            Output,
            Divisor <= 0 ? 1_000_000m : Divisor,
            Schedule,
            CacheWriteInput);
    }

    public PricePreset Clone()
    {
        return new PricePreset
        {
            Group = Group,
            Provider = Provider,
            Model = Model,
            ModelId = ModelId,
            CurrencySymbol = CurrencySymbol,
            UnitLabel = UnitLabel,
            Divisor = Divisor,
            UncachedInput = UncachedInput,
            CachedInput = CachedInput,
            CacheWriteInput = CacheWriteInput,
            Output = Output,
            Source = Source,
            Schedule = Schedule
        };
    }

    public static IReadOnlyList<PricePreset> Defaults()
    {
        return new[]
        {
            Preset("OpenAI", "GPT-5.5 Standard Short", "$", "USD / 1M tokens", 1_000_000m, 5.00m, 0.50m, 30.00m, "OpenAI API Pricing"),
            Preset("DeepSeek", "V4.1 Flash", "¥", "CNY / 1M tokens", 1_000_000m, 1.00m, 0.02m, 4.00m, DeepSeekPriceSource, schedule: PriceSchedule.DeepSeekBeijingPeakDouble),
            Preset("DeepSeek", "V4 Pro", "¥", "CNY / 1M tokens", 1_000_000m, 4.50m, 0.15m, 13.50m, DeepSeekPriceSource, schedule: PriceSchedule.DeepSeekBeijingPeakDouble),
            Preset("Xiaomi", "MiMo V2.5 Pro", "Credits", "Credits / token", 1m, 300.00m, 2.50m, 600.00m, "MiMo token plan"),
            Preset("OpenAI", "GPT-6 Astra", "$", "USD / 1M tokens", 1_000_000m, 10m, 1m, 50m, "https://developers.openai.com/api/docs/models/gpt-6-astra (Standard)", cacheWrite: 12.5m),
            Preset("OpenAI", "codex-auto-review", "$", "USD / 1M tokens", 1_000_000m, 0.20m, 0.02m, 1.20m, "用户截图参考价（2026-09-05）；未核实为 OpenAI 官方报价，可编辑"),
            Preset("OpenAI", "GPT-5.6 Sol", "$", "USD / 1M tokens", 1_000_000m, 4.00m, 0.40m, 20.00m, OpenAiPriceSource, cacheWrite: 5.00m),
            Preset("OpenAI", "GPT-5.6 Terra", "$", "USD / 1M tokens", 1_000_000m, 2.00m, 0.20m, 12.00m, OpenAiPriceSource, cacheWrite: 2.50m),
            Preset("OpenAI", "GPT-5.6 Luna", "$", "USD / 1M tokens", 1_000_000m, 0.20m, 0.02m, 1.20m, OpenAiPriceSource, cacheWrite: 0.25m),
            Preset("OpenAI", "GPT-5.5 Standard Long", "$", "USD / 1M tokens", 1_000_000m, 10.00m, 1.00m, 45.00m, "历史长上下文对比档"),
            Preset("OpenAI", "GPT-5.5 Priority Short", "$", "USD / 1M tokens", 1_000_000m, 12.50m, 1.25m, 75.00m, "OpenAI priority short context"),
            Preset("OpenAI", "GPT-5.4 Standard Short", "$", "USD / 1M tokens", 1_000_000m, 2.50m, 0.25m, 15.00m, OpenAiPriceSource),
            Preset("OpenAI", "GPT-5.4 mini Short", "$", "USD / 1M tokens", 1_000_000m, 0.75m, 0.075m, 4.50m, OpenAiPriceSource),
            Preset("OpenAI", "GPT-5.2 Reference", "$", "USD / 1M tokens", 1_000_000m, 1.75m, 0.175m, 14.00m, "价格库参考档"),
            Preset("Xiaomi", "MiMo V2.5 Pro API", "$", "USD / 1M tokens", 1_000_000m, 0.435m, 0.0036m, 0.87m, "MiMo pay-as-you-go"),
            Preset("Xiaomi", "Token Plan ¥99 / 110亿", "¥", "CNY / 1M tokens", 1_000_000m, 0.0090m, 0.0090m, 0.0090m, "99元=110亿 token 折算"),
            Preset("Kimi（月之暗面）", "K3", "$", "USD / 1M tokens", 1_000_000m, 3.00m, 0.30m, 15.00m, "Kimi API 官方价格"),
            Preset("Kimi（月之暗面）", "K2.7 Code", "¥", "CNY / 1M tokens", 1_000_000m, 6.50m, 1.30m, 27.00m, "Kimi API 官方人民币价格"),
            Preset("Kimi（月之暗面）", "K2.7 Code HighSpeed", "¥", "CNY / 1M tokens", 1_000_000m, 13.00m, 2.60m, 54.00m, "Kimi K2.7 Code 官方价格"),
            Preset("Kimi（月之暗面）", "K2.6", "¥", "CNY / 1M tokens", 1_000_000m, 6.50m, 1.10m, 27.00m, "Kimi API 官方人民币价格"),
            Preset("Kimi（月之暗面）", "K2.5", "¥", "CNY / 1M tokens", 1_000_000m, 4.00m, 0.70m, 21.00m, "Kimi API 官方人民币价格"),
            Preset("智谱/Z.AI", "GLM-5.3 Flash", "¥", "CNY / 1M tokens", 1_000_000m, 0.80m, 0.23m, 2.80m, "bigmodel.cn/pricing（2026-09 标准价；缓存存储费暂免）"),
            Preset("智谱/Z.AI", "GLM-5.2 1M", "¥", "CNY / 1M tokens", 1_000_000m, 8.00m, 2.00m, 28.00m, "bigmodel.cn/pricing"),
            Preset("DeepSeek", "V4 Pro", "¥", "CNY / 1M tokens", 1_000_000m, 4.50m, 0.15m, 13.50m, DeepSeekPriceSource, "ZCode", PriceSchedule.DeepSeekBeijingPeakDouble),
            Preset("Xiaomi", "MiMo V2.5 Pro", "Credits", "Credits / token", 1m, 300.00m, 2.50m, 600.00m, "MiMo token plan", "ZCode"),
            Preset("智谱/Z.AI", "GLM-5.1 <=32K", "¥", "CNY / 1M tokens", 1_000_000m, 6.00m, 1.30m, 24.00m, "bigmodel.cn/pricing"),
            Preset("智谱/Z.AI", "GLM-5.1 >32K", "¥", "CNY / 1M tokens", 1_000_000m, 8.00m, 2.00m, 28.00m, "bigmodel.cn/pricing"),
            Preset("智谱/Z.AI", "GLM-4.7 <=32K short out", "¥", "CNY / 1M tokens", 1_000_000m, 2.00m, 0.40m, 8.00m, "bigmodel.cn/pricing"),
            Preset("智谱/Z.AI", "GLM-4.7 <=32K long out", "¥", "CNY / 1M tokens", 1_000_000m, 3.00m, 0.60m, 14.00m, "bigmodel.cn/pricing"),
            Preset("智谱/Z.AI", "GLM-4.7 32K-200K", "¥", "CNY / 1M tokens", 1_000_000m, 4.00m, 0.80m, 16.00m, "bigmodel.cn/pricing"),
            Preset("智谱/Z.AI", "GLM-4.5-Air <=32K short out", "¥", "CNY / 1M tokens", 1_000_000m, 0.80m, 0.16m, 2.00m, "bigmodel.cn/pricing"),
            Preset("智谱/Z.AI", "GLM-4.5-Air <=32K long out", "¥", "CNY / 1M tokens", 1_000_000m, 0.80m, 0.16m, 6.00m, "bigmodel.cn/pricing"),
            Preset("智谱/Z.AI", "GLM-4.5-Air 32K-128K", "¥", "CNY / 1M tokens", 1_000_000m, 1.20m, 0.24m, 8.00m, "bigmodel.cn/pricing"),
            Preset("智谱/Z.AI", "GLM-4.7-FlashX 200K", "¥", "CNY / 1M tokens", 1_000_000m, 0.50m, 0.10m, 3.00m, "bigmodel.cn/pricing"),
            Preset("Doubao", "Seed 2.1 Pro", "¥", "CNY / 1M tokens", 1_000_000m, 6.00m, 1.20m, 30.00m, "Volcano Engine reference"),
            Preset("Doubao", "Seed 2.0 Pro", "¥", "CNY / 1M tokens", 1_000_000m, 3.20m, 0.80m, 16.00m, "火山方舟模型价格"),
            Preset("MiniMax", "M3 <=512K", "$", "USD / 1M tokens", 1_000_000m, 0.30m, 0.06m, 1.20m, "MiniMax 官方 API 价格"),
            Preset("MiniMax", "M3 512K-1M", "$", "USD / 1M tokens", 1_000_000m, 0.60m, 0.12m, 2.40m, "MiniMax 官方 API 价格"),
            Preset("MiniMax", "M2.7", "$", "USD / 1M tokens", 1_000_000m, 0.30m, 0.06m, 1.20m, "MiniMax / Tencent pricing"),
            Preset("通义千问", "Qwen3 Coder Plus <=32K", "¥", "CNY / 1M tokens", 1_000_000m, 4.00m, 0.40m, 16.00m, "阿里云百炼模型价格"),
            Preset("通义千问", "Qwen3 Coder Plus 32K-128K", "¥", "CNY / 1M tokens", 1_000_000m, 6.00m, 0.60m, 24.00m, "阿里云百炼模型价格"),
            Preset("通义千问", "Qwen3 Coder Plus 128K-256K", "¥", "CNY / 1M tokens", 1_000_000m, 10.00m, 1.00m, 40.00m, "阿里云百炼模型价格"),
            Preset("通义千问", "Qwen3 Coder Plus 256K+", "¥", "CNY / 1M tokens", 1_000_000m, 20.00m, 2.00m, 200.00m, "阿里云百炼模型价格"),
            Preset("通义千问", "Qwen3 Coder Flash <=32K", "¥", "CNY / 1M tokens", 1_000_000m, 1.00m, 0.10m, 4.00m, "阿里云百炼模型价格"),
            Preset("通义千问", "Qwen3.7 Plus <=256K", "¥", "CNY / 1M tokens", 1_000_000m, 2.00m, 0.20m, 8.00m, "阿里云百炼模型价格"),
            Preset("通义千问", "Qwen Plus <=128K", "¥", "CNY / 1M tokens", 1_000_000m, 0.80m, 0.08m, 2.00m, "阿里云百炼模型价格"),
            Preset("腾讯混元", "Hunyuan Turbo S", "¥", "CNY / 1M tokens", 1_000_000m, 0.80m, 0.08m, 2.00m, "腾讯混元官方参考"),
            Preset("腾讯混元", "Hunyuan Turbo", "¥", "CNY / 1M tokens", 1_000_000m, 0.70m, 0.07m, 1.40m, "腾讯混元官方参考"),
            Preset("腾讯混元", "Hy3", "¥", "CNY / 1M tokens", 1_000_000m, 1.00m, 0.25m, 4.00m, "腾讯云 TokenHub 官方价格"),
            Preset("Claude", "Fable 5 API", "$", "USD / 1M tokens", 1_000_000m, 10.00m, 1.00m, 50.00m, "Anthropic pricing/cache read/write", cacheWrite: 12.50m),
            Preset("Claude", "Opus 4.8 API", "$", "USD / 1M tokens", 1_000_000m, 5.00m, 0.50m, 25.00m, "Anthropic pricing/cache read/write", cacheWrite: 6.25m),
            Preset("DeepSeek", "V4 Pro", "¥", "CNY / 1M tokens", 1_000_000m, 4.50m, 0.15m, 13.50m, DeepSeekPriceSource, "Claude Code", PriceSchedule.DeepSeekBeijingPeakDouble),
            Preset("Xiaomi", "MiMo V2.5 Pro", "Credits", "Credits / token", 1m, 300.00m, 2.50m, 600.00m, "MiMo token plan", "Claude Code"),
            Preset("Claude", "Sonnet 4.8 API", "$", "USD / 1M tokens", 1_000_000m, 3.00m, 0.30m, 15.00m, "Anthropic pricing/cache read/write", cacheWrite: 3.75m),
            Preset("Claude", "Haiku 4.8 API", "$", "USD / 1M tokens", 1_000_000m, 1.00m, 0.10m, 5.00m, "Anthropic pricing/cache read/write", cacheWrite: 1.25m),
            Preset("Claude", "Sonnet 4.6 API", "$", "USD / 1M tokens", 1_000_000m, 3.00m, 0.30m, 15.00m, "Anthropic pricing/cache read/write", cacheWrite: 3.75m),
            Preset("Claude", "Sonnet 4.5 API", "$", "USD / 1M tokens", 1_000_000m, 3.00m, 0.30m, 15.00m, "Anthropic pricing/cache read/write", cacheWrite: 3.75m),
            Preset("Claude", "Opus 4.6 API", "$", "USD / 1M tokens", 1_000_000m, 5.00m, 0.50m, 25.00m, "Anthropic pricing/cache read/write", cacheWrite: 6.25m),
            Preset("Claude", "Haiku 4.5 API", "$", "USD / 1M tokens", 1_000_000m, 1.00m, 0.10m, 5.00m, "Anthropic pricing/cache read/write", cacheWrite: 1.25m),
            Preset("Claude", "Opus 4.6 Fast", "$", "USD / 1M tokens", 1_000_000m, 30.00m, 3.00m, 150.00m, "Anthropic fast mode reference", cacheWrite: 37.50m),
            Preset("xAI", "Grok 4.3", "$", "USD / 1M tokens", 1_000_000m, 1.25m, 0.25m, 2.50m, "xAI model pricing"),
            Preset("xAI", "Grok Build 0.1", "$", "USD / 1M tokens", 1_000_000m, 1.00m, 0.00m, 2.00m, "xAI coding reference")
        };
    }

    public static IReadOnlyList<PricePreset> DefaultsForGroup(string group)
    {
        var normalizedGroup = PricePresetGroups.Normalize(group);
        var result = new List<PricePreset>();
        foreach (var preset in Defaults())
        {
            if (result.Any(item => SameCatalogPreset(item, preset)))
            {
                continue;
            }

            var clone = preset.Clone();
            clone.Group = normalizedGroup;
            result.Add(clone);
        }

        return ApplyDefaultDisplayOrder(result, normalizedGroup);
    }

    private static bool ContainsIgnoreCase(string value, string pattern)
    {
        return value.Contains(pattern, StringComparison.OrdinalIgnoreCase);
    }

    private static List<PricePreset> ApplyDefaultDisplayOrder(List<PricePreset> presets, string group)
    {
        var preferred = PricePresetGroups.Normalize(group) switch
        {
            PricePresetGroups.ClaudeCode => ("Claude", "Fable 5 API"),
            PricePresetGroups.ZCode => ("智谱/Z.AI", "GLM-5.3 Flash"),
            PricePresetGroups.WorkBuddy => ("Kimi（月之暗面）", "K3"),
            PricePresetGroups.Dsh => ("DeepSeek", "V4.1 Flash"),
            _ => ("OpenAI", "GPT-5.6 Sol")
        };
        var ordered = new List<PricePreset>();
        var first = presets.FirstOrDefault(item =>
            string.Equals(item.Provider, preferred.Item1, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.Model, preferred.Item2, StringComparison.OrdinalIgnoreCase));
        if (first is not null)
        {
            ordered.Add(first);
        }

        ordered.AddRange(presets.Where(item => !ordered.Any(existing => SameCatalogPreset(existing, item))));
        return ordered;
    }

    private static bool SameCatalogPreset(PricePreset first, PricePreset second)
    {
        return string.Equals(first.Provider, second.Provider, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(first.Model, second.Model, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(first.CurrencySymbol, second.CurrencySymbol, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(first.UnitLabel, second.UnitLabel, StringComparison.OrdinalIgnoreCase) &&
               first.Divisor == second.Divisor;
    }

    private static PricePreset Preset(
        string provider,
        string model,
        string currency,
        string unit,
        decimal divisor,
        decimal input,
        decimal cached,
        decimal output,
        string source,
        string group = "",
        PriceSchedule schedule = PriceSchedule.Flat,
        decimal? cacheWrite = null)
    {
        return new PricePreset
        {
            Group = group,
            Provider = provider,
            Model = model,
            ModelId = CodexModelCost.DefaultModelId(provider, model),
            CurrencySymbol = currency,
            UnitLabel = unit,
            Divisor = divisor,
            UncachedInput = input,
            CachedInput = cached,
            CacheWriteInput = cacheWrite,
            Output = output,
            Source = source,
            Schedule = schedule
        };
    }
}

internal static class PriceSettingsStore
{
    private const string FolderName = "CodexTokenMonitor";
    private const string FileName = "price-settings.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly object SyncRoot = new();
    private static readonly Dictionary<string, SettingsState> States = new(StringComparer.OrdinalIgnoreCase);

    public static PriceSettings Current => Load();

    public static PriceSettings Defaults()
    {
        var settings = new PriceSettings
        {
            DisplayOrderVersion = 18,
            Presets = new(),
            CodexPresets = ApplyDefaultDisplayOrder(
                NormalizeGroupPresets(PricePreset.DefaultsForGroup(PricePresetGroups.Codex), PricePresetGroups.Codex),
                PricePresetGroups.Codex),
            ClaudeCodePresets = ApplyDefaultDisplayOrder(
                NormalizeGroupPresets(PricePreset.DefaultsForGroup(PricePresetGroups.ClaudeCode), PricePresetGroups.ClaudeCode),
                PricePresetGroups.ClaudeCode),
            ZCodePresets = ApplyDefaultDisplayOrder(
                NormalizeGroupPresets(PricePreset.DefaultsForGroup(PricePresetGroups.ZCode), PricePresetGroups.ZCode),
                PricePresetGroups.ZCode),
            WorkBuddyPresets = ApplyDefaultDisplayOrder(
                NormalizeGroupPresets(PricePreset.DefaultsForGroup(PricePresetGroups.WorkBuddy), PricePresetGroups.WorkBuddy),
                PricePresetGroups.WorkBuddy),
            DshPresets = ApplyDefaultDisplayOrder(
                NormalizeGroupPresets(PricePreset.DefaultsForGroup(PricePresetGroups.Dsh), PricePresetGroups.Dsh),
                PricePresetGroups.Dsh)
        };
        return settings;
    }

    public static void Save(PriceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var normalized = Normalize(settings);
        var path = GetPath();
        lock (SyncRoot)
        {
            var state = GetState(path);
            try
            {
                // A default/fallback editor snapshot is not permission to replace
                // an unreadable user file. Validate before the atomic replacement.
                state.HadFile |= File.Exists(path);
                var existing = ReadFile(path);
                if (existing is null && state.HadFile)
                    throw new FileNotFoundException("Previously observed price settings are missing.", path);
                if (existing is not null) _ = Normalize(Deserialize(existing));
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                var json = JsonSerializer.Serialize(normalized, JsonOptions);
                WriteAtomically(path, json);
                state.LastGood = normalized;
                state.LastJson = json;
                state.HadFile = true;
                state.Failure = null;
                RememberOperation(state);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                state.Failure = ex;
                RememberOperation(state);
                CacheOperationDiagnostics.Report(path, nameof(Save), ex);
                throw;
            }
        }
    }

    public static string GptSubtitle()
    {
        var name = Current.ToGptProfile().Name;
        const string prefix = "GPT-5.5 ";
        return name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? name[prefix.Length..]
            : name;
    }

    public static IReadOnlyList<PricePreset> DisplayPresetsForSource(UsageSource source, int count)
    {
        return DisplayPresetsForGroup(PricePresetGroups.ForSource(source), count);
    }

    public static IReadOnlyList<PricePreset> DisplayPresetsForGroup(string group, int count)
    {
        var candidates = Current.PresetsForGroup(group)
            .Select(item => item.Clone())
            .ToList();
        if (count <= 0)
        {
            return candidates;
        }

        if (candidates.Count < count)
        {
            candidates.AddRange(PricePreset.DefaultsForGroup(group)
                .Where(item => !candidates.Any(existing => SameCatalogPreset(existing, item)))
                .Select(item => item.Clone()));
        }

        return candidates
            .Take(Math.Max(1, count))
            .ToList();
    }

    private static bool ContainsIgnoreCase(string value, string pattern)
    {
        return value.Contains(pattern, StringComparison.OrdinalIgnoreCase);
    }

    public static PriceSettings Load(bool forceReload = false)
    {
        var path = GetPath();
        lock (SyncRoot)
        {
            var state = GetState(path);
            var operation = CacheOperationDiagnostics.CurrentOperation;
            if (!forceReload && operation is not null && state.Operations.TryGetValue(operation, out var snapshot))
            {
                if (snapshot.Failure is not null)
                    CacheOperationDiagnostics.Report(path, nameof(Load), snapshot.Failure);
                return snapshot.Settings;
            }
            if (!forceReload && operation is null && (state.LastGood is not null || state.Failure is not null))
            {
                if (state.Failure is not null)
                    CacheOperationDiagnostics.Report(path, nameof(Load), state.Failure);
                return state.LastGood ?? state.Fallback;
            }

            try
            {
                state.HadFile |= File.Exists(path);
                var json = ReadFile(path);
                if (json is null)
                {
                    if (state.HadFile)
                        throw new FileNotFoundException("Previously loaded price settings are missing.", path);
                    state.LastGood ??= Defaults();
                    state.LastJson = null;
                }
                else if (state.LastGood is null || !string.Equals(json, state.LastJson, StringComparison.Ordinal))
                {
                    var normalized = Normalize(Deserialize(json));
                    var normalizedJson = JsonSerializer.Serialize(normalized, JsonOptions);
                    if (!string.Equals(json.Trim(), normalizedJson.Trim(), StringComparison.Ordinal))
                    {
                        WriteAtomically(path, normalizedJson);
                        json = normalizedJson;
                    }
                    state.LastGood = normalized;
                    state.LastJson = json;
                    state.HadFile = true;
                }

                state.Failure = null;
                RememberOperation(state);
                return state.LastGood!;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                state.Failure = ex;
                RememberOperation(state);
                CacheOperationDiagnostics.Report(path, nameof(Load), ex);
                // Keep a stable last-good object for model-cost catalogs. Failed
                // reads are never installed as a successful pricing configuration.
                return state.LastGood ?? state.Fallback;
            }
        }
    }

    private static PriceSettings Deserialize(string json)
    {
        var settings = JsonSerializer.Deserialize<PriceSettings>(json)
            ?? throw new JsonException("Price settings must contain a settings object.");
        List<PricePreset>?[] groups = [settings.Presets, settings.CodexPresets, settings.ClaudeCodePresets,
            settings.ZCodePresets, settings.WorkBuddyPresets, settings.DshPresets];
        if (groups.Any(group => group is null || group.Any(preset => preset is null)))
            throw new JsonException("Price preset collections and their entries must not be null.");
        return settings;
    }

    private static string? ReadFile(string path)
    {
        try { return File.ReadAllText(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static SettingsState GetState(string path)
    {
        if (!States.TryGetValue(path, out var state)) States[path] = state = new SettingsState();
        return state;
    }

    private static void RememberOperation(SettingsState state)
    {
        if (CacheOperationDiagnostics.CurrentOperation is not { } operation) return;
        state.Operations.Remove(operation);
        state.Operations.Add(operation, new ReadSnapshot(state.LastGood ?? state.Fallback, state.Failure));
    }

    private sealed record ReadSnapshot(PriceSettings Settings, Exception? Failure);

    private sealed class SettingsState
    {
        public PriceSettings? LastGood { get; set; }
        private PriceSettings? fallback;
        public PriceSettings Fallback => fallback ??= Defaults();
        public string? LastJson { get; set; }
        public bool HadFile { get; set; }
        public ConditionalWeakTable<CacheOperationDiagnostics, ReadSnapshot> Operations { get; } = new();
        public Exception? Failure { get; set; }
    }

    private static void WriteAtomically(string path, string contents)
    {
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, contents);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    internal static PriceSettings Normalize(PriceSettings settings)
    {
        var defaults = Defaults();
        var codexPresets = NormalizeGroupPresets(SelectConfiguredPresets(settings, PricePresetGroups.Codex), PricePresetGroups.Codex);
        var claudePresets = NormalizeGroupPresets(SelectConfiguredPresets(settings, PricePresetGroups.ClaudeCode), PricePresetGroups.ClaudeCode);
        var zCodePresets = NormalizeGroupPresets(SelectConfiguredPresets(settings, PricePresetGroups.ZCode), PricePresetGroups.ZCode);
        var workBuddyPresets = NormalizeGroupPresets(SelectConfiguredPresets(settings, PricePresetGroups.WorkBuddy), PricePresetGroups.WorkBuddy);
        var dshPresets = NormalizeGroupPresets(SelectConfiguredPresets(settings, PricePresetGroups.Dsh), PricePresetGroups.Dsh);
        var shouldRefreshDefaults = settings.DisplayOrderVersion < defaults.DisplayOrderVersion;
        if (shouldRefreshDefaults)
        {
            codexPresets = ApplyDefaultDisplayOrder(codexPresets, PricePresetGroups.Codex);
            claudePresets = ApplyDefaultDisplayOrder(claudePresets, PricePresetGroups.ClaudeCode);
            zCodePresets = ApplyDefaultDisplayOrder(zCodePresets, PricePresetGroups.ZCode);
            workBuddyPresets = ApplyDefaultDisplayOrder(workBuddyPresets, PricePresetGroups.WorkBuddy);
            dshPresets = ApplyDefaultDisplayOrder(dshPresets, PricePresetGroups.Dsh);
        }

        var gptName = string.IsNullOrWhiteSpace(settings.GptName)
            ? defaults.GptName
            : settings.GptName.Trim();
        if (string.Equals(gptName, "GPT-5.5 Standard Short", StringComparison.OrdinalIgnoreCase) ||
            IsRetiredTieredPresetName(gptName))
        {
            gptName = defaults.GptName;
        }

        var oldGptDefault = gptName == "GPT-5.6 Sol" && settings.GptUncachedInputPerMillion == 5m &&
            settings.GptCachedInputPerMillion == .5m && settings.GptOutputPerMillion == 30m &&
            settings.GptCacheWriteInputPerMillion is null or 6.25m;

        return new PriceSettings
        {
            DisplayOrderVersion = defaults.DisplayOrderVersion,
            GptName = gptName,
            GptUncachedInputPerMillion = oldGptDefault ? defaults.GptUncachedInputPerMillion : PositiveOrDefault(settings.GptUncachedInputPerMillion, defaults.GptUncachedInputPerMillion),
            GptCachedInputPerMillion = oldGptDefault ? defaults.GptCachedInputPerMillion : PositiveOrDefault(settings.GptCachedInputPerMillion, defaults.GptCachedInputPerMillion),
            GptCacheWriteInputPerMillion = !oldGptDefault && settings.GptCacheWriteInputPerMillion is >= 0m
                ? settings.GptCacheWriteInputPerMillion
                : defaults.GptCacheWriteInputPerMillion,
            GptOutputPerMillion = oldGptDefault ? defaults.GptOutputPerMillion : PositiveOrDefault(settings.GptOutputPerMillion, defaults.GptOutputPerMillion),
            DeepSeekUncachedInputPerMillion = shouldRefreshDefaults
                ? defaults.DeepSeekUncachedInputPerMillion
                : PositiveOrDefault(settings.DeepSeekUncachedInputPerMillion, defaults.DeepSeekUncachedInputPerMillion),
            DeepSeekCachedInputPerMillion = shouldRefreshDefaults
                ? defaults.DeepSeekCachedInputPerMillion
                : PositiveOrDefault(settings.DeepSeekCachedInputPerMillion, defaults.DeepSeekCachedInputPerMillion),
            DeepSeekOutputPerMillion = shouldRefreshDefaults
                ? defaults.DeepSeekOutputPerMillion
                : PositiveOrDefault(settings.DeepSeekOutputPerMillion, defaults.DeepSeekOutputPerMillion),
            XiaomiUncachedInputCreditsPerToken = PositiveOrDefault(settings.XiaomiUncachedInputCreditsPerToken, defaults.XiaomiUncachedInputCreditsPerToken),
            XiaomiCachedInputCreditsPerToken = PositiveOrDefault(settings.XiaomiCachedInputCreditsPerToken, defaults.XiaomiCachedInputCreditsPerToken),
            XiaomiOutputCreditsPerToken = PositiveOrDefault(settings.XiaomiOutputCreditsPerToken, defaults.XiaomiOutputCreditsPerToken),
            Presets = new(),
            CodexPresets = codexPresets,
            ClaudeCodePresets = claudePresets,
            ZCodePresets = zCodePresets,
            WorkBuddyPresets = workBuddyPresets,
            DshPresets = dshPresets
        };
    }

    private static IReadOnlyList<PricePreset> SelectConfiguredPresets(PriceSettings settings, string group)
    {
        if (settings.DisplayOrderVersion < 7 && settings.Presets.Count > 0)
        {
            return SplitLegacyPresets(settings.Presets, group);
        }

        var direct = settings.PresetsForGroup(group);
        if (direct.Count > 0)
        {
            return direct;
        }

        if (settings.Presets.Count > 0)
        {
            return settings.Presets.Where(item =>
                PricePresetGroups.Normalize(string.IsNullOrWhiteSpace(item.Group) ? InferGroup(item) : item.Group) ==
                PricePresetGroups.Normalize(group)).ToList();
        }

        return PricePreset.DefaultsForGroup(group);
    }

    private static IReadOnlyList<PricePreset> SplitLegacyPresets(IReadOnlyList<PricePreset> presets, string group)
    {
        var normalizedGroup = PricePresetGroups.Normalize(group);
        var groupDefaults = PricePreset.DefaultsForGroup(normalizedGroup);
        var allDefaults = PricePreset.Defaults();

        return presets
            .Where(item => PricePresetGroups.Normalize(item.Group) == normalizedGroup)
            .Where(item =>
                PricePresetGroups.Normalize(InferGroup(item)) == normalizedGroup ||
                groupDefaults.Any(defaultItem => SameCatalogPreset(defaultItem, item)) ||
                !allDefaults.Any(defaultItem => SameCatalogPreset(defaultItem, item)))
            .ToList();
    }

    private static List<PricePreset> ApplyDefaultDisplayOrder(List<PricePreset> presets, string group)
    {
        var preferred = new (string Group, string Provider, string Model)[]
        {
            ("Codex", "OpenAI", "GPT-5.6 Sol"),
            ("Codex", "DeepSeek", "V4.1 Flash"),
            ("Codex", "Xiaomi", "MiMo V2.5 Pro"),
            ("Claude Code", "Claude", "Fable 5 API"),
            ("Claude Code", "DeepSeek", "V4.1 Flash"),
            ("Claude Code", "Xiaomi", "MiMo V2.5 Pro"),
            ("ZCode", "智谱/Z.AI", "GLM-5.3 Flash"),
            ("ZCode", "DeepSeek", "V4.1 Flash"),
            ("ZCode", "智谱/Z.AI", "GLM-5.2 1M"),
            ("ZCode", "Xiaomi", "MiMo V2.5 Pro"),
            ("WorkBuddy", "Kimi（月之暗面）", "K3"),
            ("WorkBuddy", "DeepSeek", "V4.1 Flash"),
            ("WorkBuddy", "智谱/Z.AI", "GLM-5.2 1M"),
            ("DSH", "DeepSeek", "V4.1 Flash"),
            ("DSH", "DeepSeek", "V4 Pro"),
            ("DSH", "Xiaomi", "MiMo V2.5 Pro"),
            ("DSH", "OpenAI", "GPT-5.6 Sol")
        };

        var ordered = new List<PricePreset>();
        foreach (var key in preferred)
        {
            if (PricePresetGroups.Normalize(key.Group) != PricePresetGroups.Normalize(group))
            {
                continue;
            }

            var match = presets.FirstOrDefault(item =>
                PricePresetGroups.Normalize(item.Group) == PricePresetGroups.Normalize(key.Group) &&
                string.Equals(item.Provider, key.Provider, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Model, key.Model, StringComparison.OrdinalIgnoreCase));
            if (match is not null && !ordered.Any(item => SameCatalogPreset(item, match)))
            {
                ordered.Add(match);
            }
        }

        ordered.AddRange(presets.Where(item => !ordered.Any(existing => SameCatalogPreset(existing, item))));
        return ordered;
    }

    private static List<PricePreset> NormalizeGroupPresets(IEnumerable<PricePreset>? presets, string group)
    {
        var normalizedGroup = PricePresetGroups.Normalize(group);
        var input = presets?.ToList();
        var source = (input is { Count: > 0 } ? input : PricePreset.DefaultsForGroup(normalizedGroup))
            .Concat(PricePreset.DefaultsForGroup(normalizedGroup))
            .Where(item => !string.IsNullOrWhiteSpace(item.Model))
            .Where(item => !IsRetiredPreset(item))
            .Select(item => NormalizePreset(item, normalizedGroup))
            .ToList();

        var result = new List<PricePreset>();
        foreach (var preset in source)
        {
            if (!result.Any(item => SameCatalogPreset(item, preset)))
            {
                result.Add(preset);
            }
        }

        return result;
    }

    private static PricePreset NormalizePreset(PricePreset item, string group)
    {
        var normalized = new PricePreset
        {
            Group = PricePresetGroups.Normalize(group),
            Provider = item.Provider.Trim(),
            Model = item.Model.Trim(),
            ModelId = string.IsNullOrWhiteSpace(item.ModelId)
                ? CodexModelCost.DefaultModelId(item.Provider, item.Model)
                : item.ModelId.Trim(),
            CurrencySymbol = string.IsNullOrWhiteSpace(item.CurrencySymbol) ? "$" : item.CurrencySymbol.Trim(),
            UnitLabel = string.IsNullOrWhiteSpace(item.UnitLabel) ? "1M tokens" : item.UnitLabel.Trim(),
            Divisor = item.Divisor <= 0 ? 1_000_000m : item.Divisor,
            UncachedInput = PositiveOrDefault(item.UncachedInput, 0),
            CachedInput = PositiveOrDefault(item.CachedInput, 0),
            CacheWriteInput = item.CacheWriteInput is >= 0m ? item.CacheWriteInput : null,
            Output = PositiveOrDefault(item.Output, 0),
            Source = item.Source.Trim(),
            Schedule = item.Schedule
        };

        RefreshOldOpenAiDefault(normalized);
        if (TryGetKnownCacheWritePrice(normalized, out var knownCacheWritePrice) &&
            normalized.CacheWriteInput is null)
        {
            normalized.CacheWriteInput = knownCacheWritePrice;
        }

        if (CodexModelCost.HasNoPublicPrice(normalized.ModelId) &&
            normalized.Source == CodexModelCost.PlaceholderPriceSource &&
            normalized.UncachedInput == 0 && normalized.CachedInput == 0 && normalized.Output == 0 &&
            (normalized.CacheWriteInput ?? 0) == 0)
            normalized.Source = CodexModelCost.NoPublicPriceSource;

        if (IsOfficialDeepSeekModel(normalized, "V4 Flash"))
        {
            normalized.Model = "V4.1 Flash";
        }

        if (IsOfficialDeepSeekModel(normalized, "V4.1 Flash"))
        {
            normalized.UncachedInput = 1.00m;
            normalized.CachedInput = 0.02m;
            normalized.Output = 4.00m;
            normalized.Source = PricePreset.DeepSeekPriceSource;
            normalized.Schedule = PriceSchedule.DeepSeekBeijingPeakDouble;
        }
        else if (IsOfficialDeepSeekModel(normalized, "V4 Pro"))
        {
            normalized.UncachedInput = 4.50m;
            normalized.CachedInput = 0.15m;
            normalized.Output = 13.50m;
            normalized.Source = PricePreset.DeepSeekPriceSource;
            normalized.Schedule = PriceSchedule.DeepSeekBeijingPeakDouble;
        }

        return normalized;
    }

    private static void RefreshOldOpenAiDefault(PricePreset preset)
    {
        var expectedId = CodexModelCost.DefaultModelId(preset.Provider, preset.Model);
        if (string.IsNullOrEmpty(expectedId)) expectedId = CodexModelCost.NormalizeModelId(preset.Model);
        if (preset.Provider != "OpenAI" || preset.CurrencySymbol != "$" || preset.Divisor != 1_000_000m ||
            preset.Source is not ("OpenAI API Pricing" or "OpenAI Help Center GPT-5.6 preview") ||
            !string.IsNullOrEmpty(preset.ModelId) && CodexModelCost.NormalizeModelId(preset.ModelId) != expectedId) return;
        (decimal Input, decimal Cached, decimal Output, decimal? Write) previous = preset.Model switch
        {
            "GPT-5.6 Sol" => (5m, .5m, 30m, (decimal?)6.25m),
            "GPT-5.6 Terra" => (2.5m, .25m, 15m, (decimal?)3.125m),
            "GPT-5.6 Luna" => (1m, .1m, 6m, (decimal?)1.25m),
            "GPT-5.4 Standard Short" => (5m, .5m, 30m, (decimal?)null),
            "GPT-5.4 mini Short" => (1.5m, .15m, 9m, (decimal?)null),
            _ => (0m, 0m, 0m, (decimal?)null)
        };
        if (previous.Input == 0 || preset.UncachedInput != previous.Input || preset.CachedInput != previous.Cached ||
            preset.Output != previous.Output || (preset.CacheWriteInput is not null && preset.CacheWriteInput != previous.Write)) return;
        var current = PricePreset.Defaults().First(p => p.Provider == preset.Provider && p.Model == preset.Model);
        preset.UncachedInput = current.UncachedInput;
        preset.CachedInput = current.CachedInput;
        preset.CacheWriteInput = current.CacheWriteInput;
        preset.Output = current.Output;
        preset.Source = current.Source;
    }

    private static bool TryGetKnownCacheWritePrice(PricePreset preset, out decimal price)
    {
        price = 0m;
        if (preset.Provider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase))
        {
            price = preset.Model switch
            {
                "GPT-5.6 Sol" => 5m,
                "GPT-5.6 Terra" => 2.5m,
                "GPT-5.6 Luna" => .25m,
                _ => 0m
            };
            return price > 0m;
        }

        if (!preset.Provider.Equals("Claude", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Claude's standard 5-minute prompt-cache write rate is 1.25x input.
        price = preset.UncachedInput * 1.25m;
        return true;
    }

    private static bool IsRetiredPreset(PricePreset preset)
    {
        if (IsRetiredTieredPresetName(preset.Model))
        {
            return true;
        }

        if (!preset.Provider.Contains("DeepSeek", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var model = preset.Model.Trim();
        return model.EndsWith(" 高峰", StringComparison.OrdinalIgnoreCase) ||
               model.EndsWith(" 空闲", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(model, "V4 Pro API", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOfficialDeepSeekModel(PricePreset preset, string model)
    {
        return preset.Provider.Contains("DeepSeek", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(preset.Model, model, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(preset.CurrencySymbol, "¥", StringComparison.OrdinalIgnoreCase) &&
               preset.Divisor == 1_000_000m;
    }

    private static bool IsRetiredTieredPresetName(string? model)
    {
        return string.Equals(model?.Trim(), "GPT-5.6 API", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(model?.Trim(), "GPT-5.6 Sol Auto Context", StringComparison.OrdinalIgnoreCase);
    }

    private static string InferGroup(PricePreset preset)
    {
        if (ContainsIgnoreCase(preset.Provider, "Claude") ||
            ContainsIgnoreCase(preset.Model, "Claude") ||
            ContainsIgnoreCase(preset.Model, "Opus") ||
            ContainsIgnoreCase(preset.Model, "Sonnet") ||
            ContainsIgnoreCase(preset.Model, "Haiku"))
        {
            return PricePresetGroups.ClaudeCode;
        }

        if (ContainsIgnoreCase(preset.Provider, "智谱") ||
            ContainsIgnoreCase(preset.Provider, "Z.AI") ||
            ContainsIgnoreCase(preset.Model, "GLM"))
        {
            return PricePresetGroups.ZCode;
        }

        if (ContainsIgnoreCase(preset.Provider, "WorkBuddy") ||
            ContainsIgnoreCase(preset.Model, "WorkBuddy") ||
            ContainsIgnoreCase(preset.Model, "Buddy"))
        {
            return PricePresetGroups.WorkBuddy;
        }

        return PricePresetGroups.Codex;
    }

    private static bool SameCatalogPreset(PricePreset first, PricePreset second)
    {
        return string.Equals(first.CurrencySymbol, second.CurrencySymbol, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(first.UnitLabel, second.UnitLabel, StringComparison.OrdinalIgnoreCase) &&
               first.Divisor == second.Divisor &&
               string.Equals(first.Provider, second.Provider, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(first.Model, second.Model, StringComparison.OrdinalIgnoreCase);
    }

    private static decimal PositiveOrDefault(decimal value, decimal fallback)
    {
        return value >= 0 ? value : fallback;
    }

    private static string GetPath()
    {
        return Path.Combine(MonitorCachePaths.LocalAppData, FolderName, FileName);
    }
}
