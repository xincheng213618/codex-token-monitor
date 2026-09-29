using System.Windows;
using System.Windows.Controls;

namespace CodexTokenMonitor;

public partial class MainWindow
{
    private string? CurrentRealPricePlanId()
    {
        var now = BeijingClock.Now;
        var active = currentPlanSnapshot?
            .Where(plan => plan.StartLocal <= now && plan.EndLocal > now)
            .Select(plan => RealPriceCalculator.CodexPlanId(plan.PlanName))
            .Distinct().ToArray();
        // Ambiguous overlapping or unnamed plans must not pick an arbitrary tier.
        return active is { Length: 1 } ? active[0] : null;
    }

    private RealPriceEstimate EstimateRealPrice(UsageSource source, TokenUsageBucket usage) =>
        RealPriceCalculator.Estimate(usage, source,
            source == UsageSource.Codex ? CurrentRealPricePlanId() :
            source == UsageSource.ZCode ? CurrentZCodeReferencePlanId() : null);

    private string? CurrentZCodeReferencePlanId()
    {
        if (CurrentModule() is not ZCodeUsageModule { CurrentQuotaSnapshot: { } snapshot } module)
            return null;

        var plan = ResolveSelectedPlan(module, snapshot);
        return plan.IsActive ? RealPriceCalculator.ZCodeReferencePlanId(plan.Name) : null;
    }

    private CostCardControl CreateRealPriceCard(UsageSource source, TokenUsageSummary usage)
    {
        var estimate = EstimateRealPrice(source, usage);
        var caption = source == UsageSource.ZCode && estimate.PreferredPlanId is not null
            ? "Lite ¥118 · 非高峰参考"
            : source == UsageSource.ZCode ? "套餐未匹配 · 最高参考"
            : CurrentRealPricePlanId() is null ? "最高网站参考 · 套餐未匹配"
            : estimate.UsesOtherPlans ? "当前套餐优先 · 含跨套餐参考"
            : "当前套餐 · 网站参考";
        if (source == UsageSource.ZCode && estimate.UsesOtherPlans)
            caption += " · 含跨套餐";
        if (!estimate.IsComplete) caption = "部分未覆盖 · " + caption;
        var card = new CostCardControl("真实价格（估算）", caption, estimate.Format(), estimate.Describe(), actual: false)
        {
            Tag = "real-price",
            Width = CostCardWidth,
            Margin = new Thickness(0, 0, CostCardRightMargin, 0),
            ToolTip = new TextBlock { Text = estimate.Describe(), TextWrapping = TextWrapping.Wrap, MaxWidth = 650 }
        };
        ToolTipService.SetShowDuration(card, 60_000);
        return card;
    }

    private void RefreshZCodeRealPriceCard(ZCodeUsageModule module)
    {
        if (module.TryGetDisplay(out _, out var result))
            ApplyCostCards(PriceSettingsStore.DisplayPresetsForSource(module.Source, count: 0), result.Summary);
    }
}
