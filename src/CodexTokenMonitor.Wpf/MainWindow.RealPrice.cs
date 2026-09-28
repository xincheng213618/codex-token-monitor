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
            source == UsageSource.Codex ? CurrentRealPricePlanId() : null);

    private CostCardControl CreateRealPriceCard(UsageSource source, TokenUsageSummary usage)
    {
        var estimate = EstimateRealPrice(source, usage);
        var caption = source == UsageSource.ZCode ? "免费用量 · 最高参考价"
            : CurrentRealPricePlanId() is null ? "最高网站参考 · 套餐未匹配"
            : estimate.UsesOtherPlans ? "当前套餐优先 · 含跨套餐参考"
            : "当前套餐 · 网站参考";
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
}
