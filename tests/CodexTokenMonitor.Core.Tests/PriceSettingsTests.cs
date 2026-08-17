using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class PriceSettingsTests
{
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
        Assert.Equal(50.00m, preset.Output);
        Assert.Equal(1_000_000m, preset.Divisor);
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
