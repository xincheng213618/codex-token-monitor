namespace CodexTokenMonitor;

public partial class MainWindow
{
    private UsageSource modelBreakdownSource;
    private TokenUsageSummary? modelBreakdownSummary;
    private ModelUsageBreakdownView? modelBreakdownView;

    private void SetModelBreakdownSource(UsageSource source, TokenUsageSummary? summary)
    {
        if (modelBreakdownSource != source) modelBreakdownView?.ClearModelSelection();
        modelBreakdownSource = source;
        modelBreakdownSummary = summary;
        if (ModelBreakdownTab.IsSelected) RenderModelBreakdown();
    }

    private void RenderModelBreakdown()
    {
        if (isClosed) return;
        if (modelBreakdownView is null) ModelBreakdownHost.Content = modelBreakdownView = new();

        var summary = modelBreakdownSummary;
        if (summary is null)
        {
            modelBreakdownView.Apply(null, modelBreakdownSource);
            return;
        }

        var costs = modelBreakdownSource == UsageSource.Codex
            ? CodexModelCost.Estimate(summary)
            : CodexModelCost.Estimate(summary, PricePresetGroups.ForSource(modelBreakdownSource));
        modelBreakdownView.Apply(ModelUsageBreakdown.Build(summary, costs), modelBreakdownSource);
    }
}
