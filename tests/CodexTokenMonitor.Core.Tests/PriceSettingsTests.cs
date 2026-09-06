using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class PriceSettingsTests
{
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
    [InlineData("V4 Flash", 1.50, 0.05, 4.50)]
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
            item => Assert.Equal("V4 Flash", item.Model),
            item => Assert.Equal("V4 Pro", item.Model));
    }
}
