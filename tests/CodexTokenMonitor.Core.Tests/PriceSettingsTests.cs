using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class PriceSettingsTests
{

    [Fact]
    public void Normalize_InjectsNewCatalogPresetsIntoSavedSettings()
    {
        // Simulate settings saved by an older build (version 18) whose preset
        // list predates the GLM-5.3 Flash catalog entry.
        var settings = new PriceSettings { DisplayOrderVersion = 18 };
        foreach (var group in PricePresetGroups.All)
        {
            ((List<PricePreset>)settings.PresetsForGroup(group)).RemoveAll(item => item.Model == "GLM-5.3 Flash");
        }

        Assert.DoesNotContain(settings.ZCodePresets, item => item.Model == "GLM-5.3 Flash");

        var normalized = PriceSettingsStore.Normalize(settings);

        Assert.Equal(19, normalized.DisplayOrderVersion);
        var flash = Assert.Single(normalized.ZCodePresets, item => item.Model == "GLM-5.3 Flash");
        Assert.Equal(0.80m, flash.UncachedInput);
        Assert.Equal(0.23m, flash.CachedInput);
        Assert.Equal(2.80m, flash.Output);
        Assert.Equal("glm-5.3-flash", flash.ModelId);
        Assert.Equal("GLM-5.3 Flash", normalized.ZCodePresets[0].Model);
    }

    [Fact]
    public void Normalize_AddsActualZCodeModelIdsToExistingPresets()
    {
        var settings = new PriceSettings();
        settings.ZCodePresets.Single(item => item.Model == "GLM-5.2 1M").ModelId = "";
        settings.ZCodePresets.Single(item => item.Model == "MiMo V2.5 Pro").ModelId = "";

        var normalized = PriceSettingsStore.Normalize(settings);

        Assert.Equal("glm-5.2", normalized.ZCodePresets.Single(item => item.Model == "GLM-5.2 1M").ModelId);
        Assert.Equal("mimo-v2.5-pro", normalized.ZCodePresets.Single(item => item.Model == "MiMo V2.5 Pro").ModelId);
    }

    [Fact]
    public void ZCodeDefaults_ListGlm53FlashFirst()
    {
        var presets = PricePreset.DefaultsForGroup(PricePresetGroups.ZCode);

        Assert.NotEmpty(presets);
        Assert.Equal(("智谱/Z.AI", "GLM-5.3 Flash"), (presets[0].Provider, presets[0].Model));
        Assert.Equal(0.80m, presets[0].UncachedInput);
        Assert.Equal(0.23m, presets[0].CachedInput);
        Assert.Equal(2.80m, presets[0].Output);
    }

    [Fact]
    public void OfficialRefreshUpdatesOldBuiltInsAndPreservesEditedPrices()
    {
        var settings = new PriceSettings();
        var sol = settings.CodexPresets.Single(p => p.Model == "GPT-5.6 Sol");
        sol.UncachedInput = 5m; sol.CachedInput = .5m; sol.Output = 30m; sol.CacheWriteInput = 6.25m;
        sol.Source = "OpenAI API Pricing";
        var refreshed = PriceSettingsStore.Normalize(settings).CodexPresets.Single(p => p.Model == sol.Model);
        Assert.Equal(4m, refreshed.UncachedInput);
        Assert.Equal(.4m, refreshed.CachedInput);
        Assert.Equal(20m, refreshed.Output);
        Assert.Equal(5m, refreshed.CacheWriteInput);
        sol.Source = "OpenAI Help Center GPT-5.6 preview";
        sol.ModelId = "gpt-5.6-sol";
        Assert.Equal(4m, PriceSettingsStore.Normalize(settings).CodexPresets.Single(p => p.Model == sol.Model).UncachedInput);
        sol.Source = "OpenAI API Pricing"; sol.ModelId = "";
        sol.Output = 29m;
        Assert.Equal(29m, PriceSettingsStore.Normalize(settings).CodexPresets.Single(p => p.Model == sol.Model).Output);
        sol.Output = 30m; sol.Source = "用户报价";
        Assert.Equal(30m, PriceSettingsStore.Normalize(settings).CodexPresets.Single(p => p.Model == sol.Model).Output);
        sol.Source = "OpenAI API Pricing"; sol.ModelId = "custom-sol";
        Assert.Equal(30m, PriceSettingsStore.Normalize(settings).CodexPresets.Single(p => p.Model == sol.Model).Output);
    }

    [Fact]
    public void Defaults_IncludeKimiK3OfficialPricing()
    {
        var preset = Assert.Single(PricePreset.Defaults(), item => item.Provider.Contains("Kimi") && item.Model == "K3");

        Assert.Equal("$", preset.CurrencySymbol);
        Assert.Equal(3.00m, preset.UncachedInput);
        Assert.Equal(0.30m, preset.CachedInput);
        Assert.Equal(15.00m, preset.Output);
        Assert.Equal(1_000_000m, preset.Divisor);
    }

    [Fact]
    public void Defaults_IncludeClaudeFable5OfficialPricing()
    {
        var preset = Assert.Single(PricePreset.Defaults(), item => item.Provider == "Claude" && item.Model == "Fable 5 API");

        Assert.Equal("$", preset.CurrencySymbol);
        Assert.Equal(10.00m, preset.UncachedInput);
        Assert.Equal(1.00m, preset.CachedInput);
        Assert.Equal(12.50m, preset.CacheWriteInput);
        Assert.Equal(50.00m, preset.Output);
        Assert.Equal(1_000_000m, preset.Divisor);
    }

    [Theory]
    [InlineData("GPT-5.6 Sol", 5)]
    [InlineData("GPT-5.6 Terra", 2.5)]
    [InlineData("GPT-5.6 Luna", .25)]
    public void Defaults_IncludeOpenAiCacheWritePricing(string model, double cacheWrite)
    {
        var preset = Assert.Single(
            PricePreset.Defaults(),
            item => item.Provider == "OpenAI" && item.Model == model);

        Assert.Equal((decimal)cacheWrite, preset.CacheWriteInput);
    }

    [Fact]
    public void Defaults_IncludeTencentHy3OfficialPricing()
    {
        var preset = Assert.Single(PricePreset.Defaults(), item => item.Provider == "腾讯混元" && item.Model == "Hy3");

        Assert.Equal("¥", preset.CurrencySymbol);
        Assert.Equal(1.00m, preset.UncachedInput);
        Assert.Equal(0.25m, preset.CachedInput);
        Assert.Equal(4.00m, preset.Output);
        Assert.Equal(1_000_000m, preset.Divisor);
    }

    [Theory]
    [InlineData("M3 <=512K", 0.30, 0.06, 1.20)]
    [InlineData("M3 512K-1M", 0.60, 0.12, 2.40)]
    public void Defaults_IncludeBothMiniMaxM3ContextTiers(string model, double input, double cached, double output)
    {
        var preset = Assert.Single(PricePreset.Defaults(), item => item.Provider == "MiniMax" && item.Model == model);

        Assert.Equal((decimal)input, preset.UncachedInput);
        Assert.Equal((decimal)cached, preset.CachedInput);
        Assert.Equal((decimal)output, preset.Output);
        Assert.Equal(1_000_000m, preset.Divisor);
    }

    [Theory]
    [InlineData("Claude Code", "Claude", "Fable 5 API")]
    [InlineData("WorkBuddy", "Kimi（月之暗面）", "K3")]
    public void Defaults_PromoteNewestModelForRelevantSource(string group, string provider, string model)
    {
        var first = PriceSettingsStore.Defaults().PresetsForGroup(group).First();

        Assert.Equal(provider, first.Provider);
        Assert.Equal(model, first.Model);
    }

    [Theory]
    [InlineData("V4.1 Flash", 1.00, 0.02, 4.00)]
    [InlineData("V4 Pro", 4.50, 0.15, 13.50)]
    public void Defaults_UseMergedDeepSeekPeakSchedule(string model, double input, double cached, double output)
    {
        var presets = PricePreset.Defaults()
            .Where(item => string.IsNullOrEmpty(item.Group) &&
                           item.Provider == "DeepSeek" &&
                           item.Model.StartsWith(model, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var preset = Assert.Single(presets);
        Assert.Equal((decimal)input, preset.UncachedInput);
        Assert.Equal((decimal)cached, preset.CachedInput);
        Assert.Equal((decimal)output, preset.Output);
        Assert.Equal(PriceSchedule.DeepSeekBeijingPeakDouble, preset.Schedule);
    }

    [Fact]
    public void Defaults_DshShowsOneMergedPresetPerDeepSeekModel()
    {
        var deepSeek = PriceSettingsStore.Defaults().DshPresets
            .Where(item => item.Provider == "DeepSeek")
            .ToList();

        Assert.Collection(
            deepSeek,
            item => Assert.Equal("V4.1 Flash", item.Model),
            item => Assert.Equal("V4 Pro", item.Model));
    }

    [Fact]
    public void Normalize_MigratesLegacyFlashPricingAndPromotesFlashAsComparisonDefault()
    {
        var settings = new PriceSettings
        {
            DisplayOrderVersion = 17,
            DeepSeekUncachedInputPerMillion = 4.50m,
            DeepSeekCachedInputPerMillion = 0.15m,
            DeepSeekOutputPerMillion = 13.50m
        };
        foreach (var group in PricePresetGroups.All)
        {
            var legacy = settings.PresetsForGroup(group).Single(item => item.Provider == "DeepSeek" && item.Model == "V4.1 Flash");
            legacy.Model = "V4 Flash";
            legacy.UncachedInput = 1.50m;
            legacy.CachedInput = 0.05m;
            legacy.Output = 4.50m;
            legacy.Source = "DeepSeek 官网峰谷定价（空闲价；北京时间高峰 ×2）";
        }

        var normalized = PriceSettingsStore.Normalize(settings);

        Assert.Equal(19, normalized.DisplayOrderVersion);
        Assert.Equal("DeepSeek V4.1 Flash", normalized.ToDeepSeekProfile().Name);
        Assert.Equal(1.00m, normalized.DeepSeekUncachedInputPerMillion);
        Assert.Equal(0.02m, normalized.DeepSeekCachedInputPerMillion);
        Assert.Equal(4.00m, normalized.DeepSeekOutputPerMillion);
        Assert.DoesNotContain(normalized.CodexPresets, item => item.Provider == "DeepSeek" && item.Model == "V4 Flash");
        Assert.Equal("V4.1 Flash", normalized.CodexPresets[1].Model);
        Assert.Equal("V4.1 Flash", normalized.ClaudeCodePresets[1].Model);
        Assert.Equal("V4.1 Flash", normalized.ZCodePresets[1].Model);
        Assert.Equal("V4.1 Flash", normalized.WorkBuddyPresets[1].Model);
        Assert.Equal("V4.1 Flash", normalized.DshPresets[0].Model);
    }
}
