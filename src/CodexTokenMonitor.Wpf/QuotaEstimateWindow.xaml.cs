using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CodexTokenMonitor;

public partial class QuotaEstimateWindow : Window
{
    private readonly CodexQuotaEstimate currentQuota;
    private readonly AnalysisQuerySession querySession;
    private IReadOnlyList<CodexQuotaCycle> loadedWeeklyPeriods;
    private QuotaEstimateLoadResult? loadedEstimateResult;
    private ResetOpportunitySummary? loadedResetSummary;
    private string? lastManualResult;
    private string? loadFailureStatus;
    private string? loadFailureDetail;
    private bool isLoading;
    private bool isManualEstimating;
    private bool isLocatingPeriod;

    internal QuotaEstimateWindow(
        CodexQuotaEstimate currentQuota,
        IReadOnlyList<CodexQuotaCycle>? knownWeeklyPeriods = null,
        MonitorRuntime? runtime = null)
    {
        this.currentQuota = currentQuota;
        loadedWeeklyPeriods = knownWeeklyPeriods ?? Array.Empty<CodexQuotaCycle>();
        querySession = new AnalysisQuerySession(runtime);
        InitializeComponent();
        FiveHourValue.Text = currentQuota.FiveHour is { } fiveHour ? $"{Math.Max(0m, 100m - fiveHour.UsedPercent):N0}%" : "未返回";
        WeekValue.Text = currentQuota.Week is { } week ? $"{Math.Max(0m, 100m - week.UsedPercent):N0}%" : "未返回";
        FiveHourDetail.Text = currentQuota.FiveHour is null ? "当前未返回 5h 限制；有额度数据后显示费用折算。" : "正在计算用量与费用…";
        WeekDetail.Text = currentQuota.Week is null ? "当前没有 7d 额度数据。" : "正在计算用量与费用…";
        AddResetText("正在读取重置卡…");

        Loaded += async (_, _) => await LoadRowsAsync();
        Closed += (_, _) => querySession.Dispose();
    }

    private async Task LoadRowsAsync()
    {
        if (isLoading || querySession.IsStopping) return;
        isLoading = true;

        try
        {
            SetStatus("正在加载估算...");
            if (loadedEstimateResult is null)
                HistoryEmptyText.Text = "正在读取历史周期…";
            LoadingProgress.Visibility = Visibility.Visible;

            var now = DateTimeOffset.UtcNow.ToOffset(CodexUsageReader.BeijingOffset);
            var periods = loadedWeeklyPeriods;
            var estimateTask = ObserveQueryAsync(querySession.RunAsync(
                "额度估算窗口加载",
                token => QuotaEstimateCalculator.BuildLoadResult(currentQuota, now, periods, token),
                requiresSharedIo: false));
            var resetTask = ObserveQueryAsync(querySession.RunAsync(
                "额度估算窗口重置卡",
                _ => ResetOpportunityStore.Summarize(now),
                requiresSharedIo: false));
            await Task.WhenAll(estimateTask, resetTask);
            if (querySession.IsStopping || !IsLoaded) return;

            var estimate = await estimateTask;
            var reset = await resetTask;
            if (querySession.IsStopping || !IsLoaded) return;
            var failures = new List<string>();
            var details = new List<string>();
            loadFailureStatus = null;
            loadFailureDetail = null;

            if (estimate.Result is { CacheWarnings.Count: 0 } estimateResult)
            {
                var result = estimateResult.Value;
                loadedEstimateResult = result;
                loadedWeeklyPeriods = result.Periods;
                ApplyCurrentRows(result.CurrentRows);
                WeeklyGrid.ItemsSource = result.WeeklyRows;
                HistoryCountText.Text = $"{result.WeeklyRows.Count:N0} 个周期";
                HistoryEmptyText.Text = "暂无历史周期，积累额度快照后可在这里回看。";
                if (result.WeeklyRows.Count > 0) WeeklyGrid.SelectedIndex = 0;
            }
            else
            {
                failures.Add("用量估算：" + FailureSummary(estimate.Result?.CacheWarnings, estimate.Error, loadedEstimateResult is not null));
                details.Add(FailureDetail(estimate.Result?.CacheWarnings, estimate.Error));
                if (loadedEstimateResult is null)
                {
                    HistoryEmptyText.Text = "历史用量暂不可用，恢复缓存后重新打开此窗口重试。";
                    FiveHourDetail.Text = "费用估算暂不可用，当前额度仍显示已取得的快照。";
                    WeekDetail.Text = "费用估算暂不可用，当前额度仍显示已取得的快照。";
                }
            }

            if (reset.Result is { CacheWarnings.Count: 0 } resetResult)
            {
                loadedResetSummary = resetResult.Value;
                ApplyResetOpportunityPanel(resetResult.Value, now);
            }
            else
            {
                failures.Add("重置卡：" + FailureSummary(reset.Result?.CacheWarnings, reset.Error, loadedResetSummary is not null));
                details.Add(FailureDetail(reset.Result?.CacheWarnings, reset.Error));
                if (loadedResetSummary is null)
                {
                    ResetOpportunityPanel.Children.Clear();
                    AddResetText("重置卡统计暂不可用，请稍后重新打开此窗口。");
                }
            }

            if (failures.Count > 0)
            {
                loadFailureStatus = string.Join(" ", failures);
                loadFailureDetail = string.Join(Environment.NewLine, details);
            }
            SetStatus(loadedEstimateResult is { WeeklyRows.Count: > 0 } loaded
                ? $"已加载 {loaded.WeeklyRows.Count:N0}/{loaded.PeriodCount:N0} 个历史周期"
                : "没有可展示的历史周期");
        }
        catch (Exception ex)
        {
            if (querySession.IsStopping || !IsLoaded) return;
            loadFailureStatus = "加载失败；已成功读取的结果会继续显示，重新打开此窗口可重试。";
            loadFailureDetail = ex.ToString();
            SetStatus(loadFailureStatus);
            if (loadedEstimateResult is null) HistoryEmptyText.Text = "历史用量暂不可用，请稍后重新打开此窗口。";
        }
        finally
        {
            isLoading = false;
            if (!querySession.IsStopping && IsLoaded)
                LoadingProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void SetStatus(string text, string? detail = null)
    {
        StatusText.Text = loadFailureStatus ?? text;
        StatusText.ToolTip = loadFailureDetail ?? detail;
    }

    private static string FailureSummary(IReadOnlyList<CacheWarning>? warnings, Exception? error, bool hasPreviousResult) =>
        warnings is { Count: > 0 }
            ? CacheFailureText.Summary(warnings, hasPreviousResult)
            : $"读取失败：{error?.Message ?? "查询未完成"}；" + (hasPreviousResult
                ? "当前显示上次成功结果，重新打开此窗口可重试。"
                : "本次统计暂不可用，重新打开此窗口可重试。");

    private static string FailureDetail(IReadOnlyList<CacheWarning>? warnings, Exception? error) =>
        warnings is { Count: > 0 } ? CacheFailureText.Detail(warnings) : error?.ToString() ?? "查询未完成";

    private static async Task<(AnalysisQueryResult<T>? Result, Exception? Error)> ObserveQueryAsync<T>(
        Task<AnalysisQueryResult<T>> task)
    {
        try
        {
            return (await task, null);
        }
        catch (Exception ex)
        {
            // Each parallel query is observed independently so one failure
            // cannot discard another query's successful result.
            return (null, ex);
        }
    }

    private void WeeklyGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (querySession.IsStopping || FindVisualParent<DataGridRow>(e.OriginalSource as DependencyObject) is not { } row)
        {
            return;
        }

        row.IsSelected = true;
        row.Focus();
    }

    private async void AnalyzeCycleMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (isLocatingPeriod || querySession.IsStopping || WeeklyGrid.SelectedItem is not QuotaWeeklyCycleRow selectedRow)
        {
            return;
        }

        isLocatingPeriod = true;
        try
        {
            var period = loadedWeeklyPeriods.FirstOrDefault(item => item.PeriodStart == selectedRow.PeriodStart);
            if (period is null)
            {
                SetStatus("正在定位所选周期...");
                var now = DateTimeOffset.UtcNow.ToOffset(CodexUsageReader.BeijingOffset);
                var result = await querySession.RunAsync(
                    "额度估算定位周期",
                    token => UsageSourceReaders.Codex.Cycles.ReadWeeklyCycles(currentQuota, now, token),
                    requiresSharedIo: false);
                if (querySession.IsStopping || !IsLoaded || !ReferenceEquals(WeeklyGrid.SelectedItem, selectedRow)) return;
                if (result.CacheWarnings.Count > 0)
                {
                    SetStatus(CacheFailureText.Summary(result.CacheWarnings, loadedEstimateResult is not null),
                        CacheFailureText.Detail(result.CacheWarnings));
                    return;
                }

                loadedWeeklyPeriods = loadedWeeklyPeriods.Concat(result.Value)
                    .GroupBy(item => item.PeriodStart)
                    .Select(group => group.Last())
                    .OrderBy(item => item.PeriodStart)
                    .ToArray();
                period = loadedWeeklyPeriods.FirstOrDefault(item => item.PeriodStart == selectedRow.PeriodStart);
            }

            if (querySession.IsStopping || !IsLoaded || !ReferenceEquals(WeeklyGrid.SelectedItem, selectedRow)) return;
            if (period is null)
            {
                SetStatus("无法定位所选周期");
                return;
            }

            var previousPeriod = loadedWeeklyPeriods
                .Where(item => item.PeriodStart < period.PeriodStart)
                .OrderByDescending(item => item.PeriodStart)
                .FirstOrDefault();
            var window = new QuotaCycleAnalysisWindow(period, currentQuota.Week, previousPeriod, runtime: querySession.Runtime)
            {
                Owner = this
            };
            window.Show();
            SetStatus("已打开所选周期分析");
        }
        catch (OperationCanceledException)
        {
            // The session cancels lookup when its window or application closes.
        }
        catch (Exception ex)
        {
            if (!querySession.IsStopping && IsLoaded && ReferenceEquals(WeeklyGrid.SelectedItem, selectedRow))
                SetStatus($"周期定位失败：{ex.Message}；请稍后重试。", ex.ToString());
        }
        finally
        {
            isLocatingPeriod = false;
        }
    }

    private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T match)
            {
                return match;
            }

            child = VisualTreeHelper.GetParent(child);
        }

        return null;
    }

    private void ApplyCurrentRows(IReadOnlyList<QuotaCurrentWindowRow> rows)
    {
        foreach (var row in rows)
        {
            if (string.Equals(row.Label, "5h", StringComparison.OrdinalIgnoreCase))
            {
                FiveHourValue.Text = row.RemainingText == "-" ? "未返回" : row.RemainingText;
                FiveHourDetail.Text = row.DetailText == "-" ? "当前未返回 5h 限制；有额度数据后显示费用折算。" : row.DetailText;
                FiveHourDetail.ToolTip = row.CostDetail;
                FiveHourPlan.Text = row.PlanText;
                FiveHourStable.Text = row.StableText;
            }
            else
            {
                WeekValue.Text = row.RemainingText == "-" ? "未返回" : row.RemainingText;
                WeekDetail.Text = row.DetailText == "-" ? "当前没有 7d 额度数据。" : row.DetailText;
                WeekDetail.ToolTip = row.CostDetail;
                WeekPlan.Text = row.PlanText;
                WeekStable.Text = row.StableText;
            }
        }
    }

    private void ApplyResetOpportunityPanel(ResetOpportunitySummary summary, DateTimeOffset now)
    {
        ResetOpportunityPanel.Children.Clear();

        AddResetText(
            ResetOpportunityFormatter.FormatPanelTitle(summary),
            FontWeights.Bold,
            (System.Windows.Media.Brush)FindResource("TextBrush"));

        if (summary.AvailableRecords.Count == 0)
        {
            AddResetText("无未过期重置卡");
            return;
        }

        foreach (var record in summary.AvailableRecords)
        {
            var line = new TextBlock
            {
                Text = $"{record.ExpiresLocal:MM-dd HH:mm} 到期 · 剩余 {ResetOpportunityFormatter.FormatRemaining(record.ExpiresLocal, now)}",
                ToolTip = ResetOpportunityFormatter.FormatRecordLine(record, now),
                FontSize = 11,
                Margin = new Thickness(0, 6, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            line.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            ResetOpportunityPanel.Children.Add(line);
        }
    }

    private void AddResetText(string text, FontWeight? fontWeight = null, System.Windows.Media.Brush? foreground = null)
    {
        ResetOpportunityPanel.Children.Add(new TextBlock
        {
            Text = text,
            FontWeight = fontWeight ?? FontWeights.Normal,
            Foreground = foreground ?? (System.Windows.Media.Brush)FindResource("MutedBrush"),
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 5),
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        });
    }

    private async void ManualEstimateButton_Click(object sender, RoutedEventArgs e)
    {
        if (isManualEstimating || querySession.IsStopping) return;
        isManualEstimating = true;
        ManualEstimateButton.IsEnabled = false;
        if (lastManualResult is null) ManualResultText.Text = "正在估算...";

        try
        {
            var fromRemaining = ManualFromBox.Value ?? 90m;
            var toRemaining = ManualToBox.Value ?? 85m;
            var result = await querySession.RunAsync(
                "手动额度估算",
                token => QuotaEstimateCalculator.BuildManualWeekEstimate(
                    currentQuota,
                    fromRemaining,
                    toRemaining,
                    token),
                requiresSharedIo: false);
            if (querySession.IsStopping || !IsLoaded)
            {
                return;
            }

            if (result.CacheWarnings.Count > 0)
            {
                var warning = CacheFailureText.Summary(result.CacheWarnings, lastManualResult is not null);
                ManualResultText.Text = lastManualResult is null ? warning : $"{lastManualResult}{Environment.NewLine}{warning}";
                ManualResultText.ToolTip = CacheFailureText.Detail(result.CacheWarnings);
                return;
            }

            lastManualResult = result.Value;
            ManualResultText.Text = result.Value;
            ManualResultText.ToolTip = null;
        }
        catch (OperationCanceledException)
        {
            // Closing the session cancels this query without publishing a result.
        }
        catch (Exception ex)
        {
            if (!querySession.IsStopping && IsLoaded)
            {
                var failure = FailureSummary(null, ex, lastManualResult is not null);
                ManualResultText.Text = lastManualResult is null ? failure : $"{lastManualResult}{Environment.NewLine}{failure}";
                ManualResultText.ToolTip = ex.ToString();
            }
        }
        finally
        {
            isManualEstimating = false;
            if (!querySession.IsStopping && IsLoaded)
            {
                ManualEstimateButton.IsEnabled = true;
            }
        }
    }

    private void QuotaCurveButton_Click(object sender, RoutedEventArgs e)
    {
        if (querySession.IsStopping) return;
        var window = new QuotaCostCurveWindow(currentQuota, loadedWeeklyPeriods, runtime: querySession.Runtime)
        {
            Owner = this
        };
        window.Show();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
