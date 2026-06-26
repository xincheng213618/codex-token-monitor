using System.Globalization;
using System.Text.Json;

namespace CodexTokenMonitor;

internal sealed class PriceSettings
{
    public string GptName { get; set; } = "GPT-5.5 Standard Short";
    public decimal GptUncachedInputPerMillion { get; set; } = 5.00m;
    public decimal GptCachedInputPerMillion { get; set; } = 0.50m;
    public decimal GptOutputPerMillion { get; set; } = 30.00m;

    public decimal DeepSeekUncachedInputPerMillion { get; set; } = 3.00m;
    public decimal DeepSeekCachedInputPerMillion { get; set; } = 0.025m;
    public decimal DeepSeekOutputPerMillion { get; set; } = 6.00m;

    public decimal XiaomiUncachedInputCreditsPerToken { get; set; } = 300.00m;
    public decimal XiaomiCachedInputCreditsPerToken { get; set; } = 2.50m;
    public decimal XiaomiOutputCreditsPerToken { get; set; } = 600.00m;
    public List<PricePreset> Presets { get; set; } = PricePreset.Defaults().ToList();

    public PriceProfile ToGptProfile()
    {
        return new PriceProfile(
            string.IsNullOrWhiteSpace(GptName) ? "GPT-5.5 Standard Short" : GptName.Trim(),
            "$",
            GptUncachedInputPerMillion,
            GptCachedInputPerMillion,
            GptOutputPerMillion,
            1_000_000m);
    }

    public PriceProfile ToDeepSeekProfile()
    {
        return new PriceProfile(
            "DeepSeek V4 Pro",
            "¥",
            DeepSeekUncachedInputPerMillion,
            DeepSeekCachedInputPerMillion,
            DeepSeekOutputPerMillion,
            1_000_000m);
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
            GptName = GptName,
            GptUncachedInputPerMillion = GptUncachedInputPerMillion,
            GptCachedInputPerMillion = GptCachedInputPerMillion,
            GptOutputPerMillion = GptOutputPerMillion,
            DeepSeekUncachedInputPerMillion = DeepSeekUncachedInputPerMillion,
            DeepSeekCachedInputPerMillion = DeepSeekCachedInputPerMillion,
            DeepSeekOutputPerMillion = DeepSeekOutputPerMillion,
            XiaomiUncachedInputCreditsPerToken = XiaomiUncachedInputCreditsPerToken,
            XiaomiCachedInputCreditsPerToken = XiaomiCachedInputCreditsPerToken,
            XiaomiOutputCreditsPerToken = XiaomiOutputCreditsPerToken,
            Presets = Presets.Select(item => item.Clone()).ToList()
        };
    }
}

internal sealed class PricePreset
{
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public string CurrencySymbol { get; set; } = "$";
    public string UnitLabel { get; set; } = "1M tokens";
    public decimal Divisor { get; set; } = 1_000_000m;
    public decimal UncachedInput { get; set; }
    public decimal CachedInput { get; set; }
    public decimal Output { get; set; }
    public string Source { get; set; } = "";

    public string DisplayName => string.IsNullOrWhiteSpace(Provider) ? Model : $"{Provider} {Model}".Trim();

    public PriceProfile ToProfile()
    {
        return new PriceProfile(
            DisplayName,
            string.IsNullOrWhiteSpace(CurrencySymbol) ? "$" : CurrencySymbol,
            UncachedInput,
            CachedInput,
            Output,
            Divisor <= 0 ? 1_000_000m : Divisor);
    }

    public PricePreset Clone()
    {
        return new PricePreset
        {
            Provider = Provider,
            Model = Model,
            CurrencySymbol = CurrencySymbol,
            UnitLabel = UnitLabel,
            Divisor = Divisor,
            UncachedInput = UncachedInput,
            CachedInput = CachedInput,
            Output = Output,
            Source = Source
        };
    }

    public static IReadOnlyList<PricePreset> Defaults()
    {
        return new[]
        {
            Preset("OpenAI", "GPT-5.5 Standard Short", "$", "USD / 1M tokens", 1_000_000m, 5.00m, 0.50m, 30.00m, "OpenAI API Pricing"),
            Preset("OpenAI", "GPT-5.5 Standard Long", "$", "USD / 1M tokens", 1_000_000m, 10.00m, 1.00m, 45.00m, "历史长上下文对比档"),
            Preset("DeepSeek", "V4 Pro", "¥", "CNY / 1M tokens", 1_000_000m, 3.00m, 0.025m, 6.00m, "当前监控默认档"),
            Preset("DeepSeek", "V4 Pro API", "$", "USD / 1M tokens", 1_000_000m, 0.435m, 0.0036m, 0.87m, "DeepSeek/OpenRouter reference"),
            Preset("Xiaomi", "MiMo V2.5 Pro", "Credits", "Credits / token", 1m, 300.00m, 2.50m, 600.00m, "MiMo token plan"),
            Preset("Xiaomi", "MiMo V2.5 Pro API", "$", "USD / 1M tokens", 1_000_000m, 0.435m, 0.0036m, 0.87m, "MiMo pay-as-you-go"),
            Preset("Xiaomi", "Token Plan ¥99 / 110亿", "¥", "CNY / 1M tokens", 1_000_000m, 0.0090m, 0.0090m, 0.0090m, "99元=110亿 token 折算"),
            Preset("Kimi", "K2.6", "$", "USD / 1M tokens", 1_000_000m, 0.95m, 0.16m, 4.00m, "Kimi API pricing"),
            Preset("Kimi", "K2.5", "$", "USD / 1M tokens", 1_000_000m, 0.60m, 0.10m, 3.00m, "Kimi API pricing"),
            Preset("Z.AI", "GLM-4.6", "$", "USD / 1M tokens", 1_000_000m, 0.60m, 0.11m, 2.20m, "Z.AI pricing"),
            Preset("Doubao", "Seed 2.1 Pro", "¥", "CNY / 1M tokens", 1_000_000m, 6.00m, 1.20m, 30.00m, "Volcano Engine reference"),
            Preset("Doubao", "Seed 2.0 Pro", "¥", "CNY / 1M tokens", 1_000_000m, 3.20m, 0.80m, 16.00m, "火山方舟模型价格"),
            Preset("MiniMax", "M3 <=512k", "$", "USD / 1M tokens", 1_000_000m, 0.30m, 0.06m, 1.20m, "MiniMax Pay-as-you-go"),
            Preset("MiniMax", "M2.7", "$", "USD / 1M tokens", 1_000_000m, 0.30m, 0.06m, 1.20m, "MiniMax / Tencent pricing"),
            Preset("Qwen", "Qwen3 Coder Plus", "$", "USD / 1M tokens", 1_000_000m, 1.00m, 0.20m, 5.00m, "Qwen reference"),
            Preset("Qwen", "Qwen3 Coder Next", "$", "USD / 1M tokens", 1_000_000m, 0.11m, 0.036m, 0.80m, "Qwen reference"),
            Preset("Tencent", "Hunyuan Turbo S", "¥", "CNY / 1M tokens", 1_000_000m, 0.80m, 0.08m, 2.00m, "Tencent Hunyuan reference"),
            Preset("Claude", "Sonnet 4.6 API", "$", "USD / 1M tokens", 1_000_000m, 3.00m, 0.30m, 15.00m, "Anthropic pricing/cache read"),
            Preset("Claude", "Opus 4.8 API", "$", "USD / 1M tokens", 1_000_000m, 5.00m, 0.50m, 25.00m, "Anthropic pricing/cache read"),
            Preset("xAI", "Grok 4.3", "$", "USD / 1M tokens", 1_000_000m, 1.25m, 0.25m, 2.50m, "xAI model pricing"),
            Preset("xAI", "Grok Build 0.1", "$", "USD / 1M tokens", 1_000_000m, 1.00m, 0.00m, 2.00m, "xAI coding reference")
        };
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
        string source)
    {
        return new PricePreset
        {
            Provider = provider,
            Model = model,
            CurrencySymbol = currency,
            UnitLabel = unit,
            Divisor = divisor,
            UncachedInput = input,
            CachedInput = cached,
            Output = output,
            Source = source
        };
    }
}

internal static class PriceSettingsStore
{
    private const string FolderName = "CodexTokenMonitor";
    private const string FileName = "price-settings.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    static PriceSettingsStore()
    {
        Current = Load();
    }

    public static PriceSettings Current { get; private set; }

    public static PriceSettings Defaults()
    {
        return new PriceSettings();
    }

    public static void Save(PriceSettings settings)
    {
        var normalized = Normalize(settings);
        var path = GetPath();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(normalized, JsonOptions));
        Current = normalized;
    }

    public static string GptSubtitle()
    {
        var name = Current.ToGptProfile().Name;
        const string prefix = "GPT-5.5 ";
        return name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? name[prefix.Length..]
            : name;
    }

    private static PriceSettings Load()
    {
        try
        {
            var path = GetPath();
            if (File.Exists(path))
            {
                var settings = JsonSerializer.Deserialize<PriceSettings>(File.ReadAllText(path));
                if (settings is not null)
                {
                    return Normalize(settings);
                }
            }
        }
        catch
        {
            // Invalid local settings should not prevent the monitor from opening.
        }

        return Defaults();
    }

    private static PriceSettings Normalize(PriceSettings settings)
    {
        var defaults = Defaults();
        return new PriceSettings
        {
            GptName = string.IsNullOrWhiteSpace(settings.GptName) ? defaults.GptName : settings.GptName.Trim(),
            GptUncachedInputPerMillion = PositiveOrDefault(settings.GptUncachedInputPerMillion, defaults.GptUncachedInputPerMillion),
            GptCachedInputPerMillion = PositiveOrDefault(settings.GptCachedInputPerMillion, defaults.GptCachedInputPerMillion),
            GptOutputPerMillion = PositiveOrDefault(settings.GptOutputPerMillion, defaults.GptOutputPerMillion),
            DeepSeekUncachedInputPerMillion = PositiveOrDefault(settings.DeepSeekUncachedInputPerMillion, defaults.DeepSeekUncachedInputPerMillion),
            DeepSeekCachedInputPerMillion = PositiveOrDefault(settings.DeepSeekCachedInputPerMillion, defaults.DeepSeekCachedInputPerMillion),
            DeepSeekOutputPerMillion = PositiveOrDefault(settings.DeepSeekOutputPerMillion, defaults.DeepSeekOutputPerMillion),
            XiaomiUncachedInputCreditsPerToken = PositiveOrDefault(settings.XiaomiUncachedInputCreditsPerToken, defaults.XiaomiUncachedInputCreditsPerToken),
            XiaomiCachedInputCreditsPerToken = PositiveOrDefault(settings.XiaomiCachedInputCreditsPerToken, defaults.XiaomiCachedInputCreditsPerToken),
            XiaomiOutputCreditsPerToken = PositiveOrDefault(settings.XiaomiOutputCreditsPerToken, defaults.XiaomiOutputCreditsPerToken),
            Presets = NormalizePresets(settings.Presets)
        };
    }

    private static List<PricePreset> NormalizePresets(List<PricePreset>? presets)
    {
        var result = (presets is { Count: > 0 } ? presets : PricePreset.Defaults())
            .Where(item => !string.IsNullOrWhiteSpace(item.Model))
            .Select(item => new PricePreset
            {
                Provider = item.Provider.Trim(),
                Model = item.Model.Trim(),
                CurrencySymbol = string.IsNullOrWhiteSpace(item.CurrencySymbol) ? "$" : item.CurrencySymbol.Trim(),
                UnitLabel = string.IsNullOrWhiteSpace(item.UnitLabel) ? "1M tokens" : item.UnitLabel.Trim(),
                Divisor = item.Divisor <= 0 ? 1_000_000m : item.Divisor,
                UncachedInput = PositiveOrDefault(item.UncachedInput, 0),
                CachedInput = PositiveOrDefault(item.CachedInput, 0),
                Output = PositiveOrDefault(item.Output, 0),
                Source = item.Source.Trim()
            })
            .ToList();

        foreach (var preset in PricePreset.Defaults())
        {
            if (!result.Any(item =>
                    string.Equals(item.Provider, preset.Provider, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(item.Model, preset.Model, StringComparison.OrdinalIgnoreCase)))
            {
                result.Add(preset.Clone());
            }
        }

        return result;
    }

    private static decimal PositiveOrDefault(decimal value, decimal fallback)
    {
        return value >= 0 ? value : fallback;
    }

    private static string GetPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, FolderName, FileName);
    }
}
