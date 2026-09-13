using System.IO;
using System.Windows.Controls;

namespace CodexTokenMonitor;

public partial class MainWindow
{
    private Task settingsSummaryRefreshTask = Task.CompletedTask;
    private bool settingsSummaryRefreshPending;
    private IReadOnlyList<SubscriptionPlanRecord>? currentPlanSnapshot;
    private IReadOnlyList<ResetOpportunityRecord>? currentResetSnapshot;
    private IReadOnlyList<CacheWarning> currentPlanWarnings = Array.Empty<CacheWarning>();
    private IReadOnlyList<CacheWarning> currentResetWarnings = Array.Empty<CacheWarning>();

    // Ordinary refreshes share the current read. A completed settings edit can
    // request one subsequent read so an older in-flight snapshot cannot win.
    private Task RefreshSettingsSummaryAsync(bool refreshAfterCurrent = false)
    {
        if (isClosed || runtime.IsStopping) return Task.CompletedTask;
        if (!settingsSummaryRefreshTask.IsCompleted)
        {
            settingsSummaryRefreshPending |= refreshAfterCurrent;
            return settingsSummaryRefreshTask;
        }

        settingsSummaryRefreshTask = runtime.Run("设置摘要刷新", RefreshSettingsSummaryCoreAsync);
        return settingsSummaryRefreshTask;
    }

    private async Task RefreshSettingsSummaryCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            do
            {
                settingsSummaryRefreshPending = false;
                var plansTask = Task.Run(() => ReadSettingsSnapshot(
                    "读取套餐设置", () => SubscriptionPlanStore.Load(), cancellationToken), cancellationToken);
                var resetsTask = Task.Run(() => ReadSettingsSnapshot(
                    "读取重置卡设置", () => ResetOpportunityStore.Load(), cancellationToken), cancellationToken);
                await Task.WhenAll(plansTask, resetsTask);
                cancellationToken.ThrowIfCancellationRequested();
                if (isClosed) return;

                var plans = await plansTask;
                var resets = await resetsTask;
                currentPlanWarnings = plans.Warnings;
                currentResetWarnings = resets.Warnings;
                if (plans.Warnings.Count == 0) currentPlanSnapshot = plans.Snapshot;
                if (resets.Warnings.Count == 0) currentResetSnapshot = resets.Snapshot;
                ApplyCurrentPlanSummary();
                ApplyResetOpportunitySummary();
                ApplyResetPaceSummary(CurrentCodexModule().CurrentQuotaEstimate?.Week);
            }
            while (settingsSummaryRefreshPending && !isClosed);
        }
        catch (OperationCanceledException) when (runtime.IsStopping || isClosed)
        {
            // The runtime retains the task until both settings reads have ended.
        }
    }

    private static SettingsSnapshotRead<T> ReadSettingsSnapshot<T>(
        string operation,
        Func<T> read,
        CancellationToken cancellationToken) where T : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var diagnostics = CacheOperationDiagnostics.Begin();
        T? snapshot = null;
        try
        {
            snapshot = read();
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            CacheOperationDiagnostics.Report(MonitorSettingsDatabase.Path, operation, ex);
        }

        return new(snapshot, diagnostics.Warnings);
    }

    private static IReadOnlyList<CacheWarning> ReadPriceSettingsForUsage(
        UsageSource source,
        UsageQueryResult? result,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var diagnostics = CacheOperationDiagnostics.Begin();
        try
        {
            var settings = PriceSettingsStore.Load();
            if (diagnostics.Warnings.Count == 0 && source == UsageSource.Codex && result is not null)
            {
                var updated = settings.Clone();
                if (CodexModelCost.AddMissingPresets(updated, result.Summary.ModelUsage.Keys) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    PriceSettingsStore.Save(updated);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (diagnostics.Warnings.Count == 0)
                CacheOperationDiagnostics.Report(
                    Path.Combine(MonitorCachePaths.LocalAppData, "CodexTokenMonitor", "price-settings.json"),
                    "读取或更新价格设置", ex);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return diagnostics.Warnings;
    }

    private void ApplyCurrentPlanSummary()
    {
        if (currentPlanSnapshot is null)
        {
            ApplyMissingSettingsState(PlanSpendValue, PlanSpendDetail, currentPlanWarnings);
            return;
        }

        var now = DateTimeOffset.UtcNow.ToOffset(CodexUsageReader.BeijingOffset);
        var active = currentPlanSnapshot
            .Where(item => item.StartLocal <= now && item.EndLocal > now)
            .OrderByDescending(item => item.StartLocal)
            .ToList();
        PlanSpendValue.Text = active.Count == 0
            ? "-" : string.Join(" / ", active.Select(item => item.PlanName).Distinct());
        PlanSpendDetail.Text = active.Count == 0 ? "" : FormatCny(active.Sum(item => item.AmountCny));
        ApplySettingsWarning(PlanSpendValue, PlanSpendDetail, currentPlanWarnings);
    }

    private void ApplyResetOpportunitySummary()
    {
        if (currentResetSnapshot is null)
        {
            ApplyMissingSettingsState(ResetOpportunityValue, ResetOpportunityDetail, currentResetWarnings);
            return;
        }

        var summary = CurrentResetSummary();
        ResetOpportunityValue.Text = ResetOpportunityFormatter.FormatCompactSummary(summary);
        ResetOpportunityDetail.Text = summary.AvailableRecords.Count == 0 ? "" : $"{summary.AvailableCount:N0} 张可用";
        ApplySettingsWarning(ResetOpportunityValue, ResetOpportunityDetail, currentResetWarnings);
    }

    private void ApplyResetPaceSummary(CodexQuotaWindowEstimate? week)
    {
        if (week is null)
        {
            ResetPaceValue.Text = "-";
            ResetPaceDetail.Text = "";
            ResetPaceValue.ToolTip = null;
            ResetPaceDetail.ToolTip = null;
            return;
        }

        if (currentResetSnapshot is null)
        {
            ApplyMissingSettingsState(ResetPaceValue, ResetPaceDetail, currentResetWarnings);
            return;
        }

        var report = QuotaPaceAnalyzer.Analyze(week, CurrentResetSummary());
        ResetPaceValue.Text = report.Rating;
        ResetPaceDetail.Text =
            $"已{report.UsedPercent:N0}% / 应{report.ExpectedUsedPercent:N0}% · " +
            $"{QuotaPaceAnalyzer.FormatPaceDelta(report.DeltaPercent)} · {report.DetailText}";
        ApplySettingsWarning(ResetPaceValue, ResetPaceDetail, currentResetWarnings);
    }

    private ResetOpportunitySummary CurrentResetSummary()
    {
        var now = DateTimeOffset.UtcNow.ToOffset(CodexUsageReader.BeijingOffset);
        var available = (currentResetSnapshot ?? Array.Empty<ResetOpportunityRecord>())
            .Where(item => !item.IsUsed && item.ExpiresLocal > now)
            .OrderBy(item => item.ExpiresLocal)
            .ToList();
        return new(available.Count, available.FirstOrDefault()?.ExpiresLocal, available);
    }

    private static void ApplyMissingSettingsState(TextBlock value, TextBlock detail, IReadOnlyList<CacheWarning> warnings)
    {
        value.Text = warnings.Count > 0 ? "暂不可用" : "读取中";
        detail.Text = warnings.Count > 0 ? "设置读取失败" : "";
        var tooltip = warnings.Count > 0
            ? CacheFailureText.Summary(warnings, hasPreviousResult: false) + Environment.NewLine + CacheFailureText.Detail(warnings)
            : "正在读取已保存的设置";
        value.ToolTip = tooltip;
        detail.ToolTip = tooltip;
    }

    private static void ApplySettingsWarning(TextBlock value, TextBlock detail, IReadOnlyList<CacheWarning> warnings)
    {
        if (warnings.Count == 0)
        {
            value.ToolTip = value.Text;
            detail.ToolTip = detail.Text;
            return;
        }

        detail.Text = string.IsNullOrWhiteSpace(detail.Text)
            ? "读取失败，沿用上次设置" : detail.Text + " · 沿用上次设置";
        var tooltip = CacheFailureText.Summary(warnings, hasPreviousResult: true) + Environment.NewLine + CacheFailureText.Detail(warnings);
        value.ToolTip = tooltip;
        detail.ToolTip = tooltip;
    }

    private sealed record SettingsSnapshotRead<T>(T? Snapshot, IReadOnlyList<CacheWarning> Warnings) where T : class;
}
