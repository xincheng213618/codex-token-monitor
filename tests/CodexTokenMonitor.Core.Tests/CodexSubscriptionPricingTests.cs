using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class CodexSubscriptionPricingTests
{
    private static readonly PriceProfile ApiProfile =
        new("API 参考档", "$", 2m, 0.2m, 8m, 1_000_000m);

    [Fact]
    public void GetProfile_SolModel_UsesFixedSubscriptionReference()
    {
        var profile = CodexSubscriptionPricing.GetProfile("gpt-5.6-sol", ApiProfile);

        Assert.Equal("Sol 订阅额度参考", profile.Name);
        Assert.Equal(5m, profile.UncachedInputPerMillion);
        Assert.Equal(0.5m, profile.CachedInputPerMillion);
        Assert.Equal(30m, profile.OutputPerMillion);
        Assert.Equal(6.25m, profile.CacheWriteInputPerMillion);
    }

    [Fact]
    public void GetProfile_BareSolName_UsesFixedSubscriptionReference()
    {
        Assert.Equal("Sol 订阅额度参考", CodexSubscriptionPricing.GetProfile("gpt-5.6", ApiProfile).Name);
    }

    [Fact]
    public void GetProfile_DatedSolSuffix_StripsDateBeforeMatching()
    {
        var profile = CodexSubscriptionPricing.GetProfile("gpt-5.6-sol-2026-08-15", ApiProfile);

        Assert.Equal("Sol 订阅额度参考", profile.Name);
    }

    [Fact]
    public void GetProfile_OtherModel_PassesApiProfileThrough()
    {
        var profile = CodexSubscriptionPricing.GetProfile("gpt-5.6-mini", ApiProfile);

        Assert.Same(ApiProfile, profile);
    }
}
