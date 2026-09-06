using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class MonitorSettingsTests
{
    [Theory]
    [InlineData(2026, 1, 31, 2026, 2, 28)]
    [InlineData(2028, 1, 31, 2028, 2, 29)]
    [InlineData(2026, 12, 2, 2027, 1, 2)]
    public void MonthlyPlanUsesCalendarMonthAndKeepsUserAmount(int y, int m, int d, int ey, int em, int ed)
    {
        var plan = SubscriptionPlanStore.CreateMonthly(new(y, m, d, 18, 30, 0, TimeSpan.FromHours(8)), "Pro 20x", 1380);
        Assert.Equal(new DateTimeOffset(ey, em, ed, 18, 30, 0, TimeSpan.FromHours(8)), plan.EndLocal);
        Assert.Equal(1380, plan.AmountCny);
        Assert.Equal("Pro 20x", plan.PlanName);
    }

    [Fact]
    public void SharingStartsByDefaultAndExplicitOptOutPersists()
    {
        using var scope = MonitorCachePaths.PushLocalAppDataRoot(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        Assert.True(CodexDataSharingSettings.Load().AutoStart);
        var settings = CodexDataSharingSettings.Load();
        settings.AutoStart = false;
        settings.Save();
        var loaded = CodexDataSharingSettings.Load();
        Assert.False(loaded.AutoStart);
        Assert.Equal(settings.AccessKey, loaded.AccessKey);
        Assert.True(System.Text.Json.JsonSerializer.Deserialize<CodexDataSharingSettings>("{\"Port\":36666}")!.AutoStart);
    }

    [Fact]
    public void DefaultPlansCoverPaidMonthsWithoutInventingFuturePayments()
    {
        var now = new DateTimeOffset(2026, 9, 5, 0, 0, 0, TimeSpan.FromHours(8));
        var plans = SubscriptionPlanStore.Defaults(now);
        var pro = plans.Where(p => p.PlanName == "Pro 20x").ToArray();
        Assert.Equal(4, pro.Length);
        Assert.All(pro, p => { Assert.Equal(2, p.StartLocal.Day); Assert.Equal(p.StartLocal.AddMonths(1), p.EndLocal); Assert.Equal(1380m, p.AmountCny); });
        Assert.Single(pro, p => p.StartLocal <= now && p.EndLocal > now);
    }

    [Fact]
    public void ClearingUsageCachePreservesPlansAndResetCardsOnDisk()
    {
        var root = Path.Combine(Path.GetTempPath(), "MonitorSettingsTests-" + Guid.NewGuid().ToString("N"));
        using var scope = MonitorCachePaths.PushLocalAppDataRoot(root);
        var start = new DateTimeOffset(2026, 9, 2, 0, 0, 0, TimeSpan.FromHours(8));
        SubscriptionPlanStore.Save(new[] { new SubscriptionPlanRecord { Id = "custom", StartLocal = start, EndLocal = start.AddMonths(1), PlanName = "Pro 20x", AmountCny = 1380 } });
        ResetOpportunityStore.Save(new[] { new ResetOpportunityRecord { Id = "card", GrantedLocal = start, ExpiresLocal = start.AddDays(30) } });
        _ = UsageCacheStore.Load("CodexTokenMonitor");
        UsageCacheStore.Delete("CodexTokenMonitor");
        using (MonitorCachePaths.PushLocalAppDataRoot(Path.Combine(root,"other")))
        {
            _ = SubscriptionPlanStore.Load(); _ = ResetOpportunityStore.Load();
        }
        Assert.Equal("custom", Assert.Single(SubscriptionPlanStore.Load()).Id);
        Assert.Equal("card", Assert.Single(ResetOpportunityStore.Load()).Id);
        Assert.True(File.Exists(MonitorSettingsDatabase.Path));
    }
}
