using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CodexTokenMonitor;

public partial class QuotaEstimateWindow : Window
{
    private readonly CodexQuotaEstimate currentQuota;
    private readonly IReadOnlyList<CodexQuotaCycle> knownWeeklyPeriods;
    private readonly SemaphoreSlim? usageReadGate;
    private readonly QuotaCostCurveControl embeddedCurveControl = new();
    private QuotaCostCurveResult? loadedCurveResult;
    private CancellationTokenSource? loadCancellation;

    internal QuotaEstimateWindow(
        CodexQuotaEstimate currentQuota,
        IReadOnlyList<CodexQuotaCycle>? knownWeeklyPeriods = null,
        SemaphoreSlim? usageReadGate = null)
    {
        this.currentQuota = currentQuota;
        this.knownWeeklyPeriods = knownWeeklyPeriods ?? Array.Empty<CodexQuotaCycle>();
        this.usageReadGate = usageReadGate;
        InitializeComponent();
        EmbeddedCurveHost.Content = embeddedCurveControl;
        ApplyResetOpportunityPanel();

        Loaded += async (_, _) => await LoadRowsAsync();
        Closed += (_, _) =>
        {
            loadCancellation?.Cancel();
            loadCancellation?.Dispose();
            loadCancellation = null;
        };
    }

    private async Task LoadRowsAsync()
    {
        loadCancellation?.Cancel();
        loadCancellation?.Dispose();
        loadCancellation = new CancellationTokenSource();
        var cancellationToken = loadCancellation.Token;
        Task<QuotaEstimateLoadResult>? estimateTask = null;
        Task<QuotaCostCurveResult>? curveTask = null;

        try
        {
            StatusText.Text = "正在加载估算...";
            WeeklyGrid.ItemsSource = null;

            var now = DateTimeOffset.UtcNow.ToOffset(CodexUsageReader.BeijingOffset);
            estimateTask = Task.Run(
                () => QuotaEstimateCalculator.BuildLoadResult(
                    currentQuota,
                    now,
                    knownWeeklyPeriods,
                    cancellationToken),
                cancellationToken);
            curveTask = Task.Run(
                () => QuotaCostCurveCalculator.Build(
                    currentQuota,
                    knownWeeklyPeriods,
                    cancellationToken,
                    usageReadGate),
                cancellationToken);
            var result = await estimateTask;
            cancellationToken.ThrowIfCancellationRequested();

            ApplyCurrentRows(result.CurrentRows);
            WeeklyGrid.ItemsSource = result.WeeklyRows;
            if (result.WeeklyRows.Count > 0)
            {
                WeeklyGrid.SelectedIndex = 0;
            }
            StatusText.Text = "历史周期已加载，正在统计额度曲线...";

            var curveResult = await curveTask;
            cancellationToken.ThrowIfCancellationRequested();
            loadedCurveResult = curveResult;
            var plans = curveResult.Curves
                .Select(item => item.PlanName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(item => string.Equals(item, curveResult.SelectedPlan, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(item => item)
                .ToList();
            CurvePlanComboBox.ItemsSource = plans;
            CurvePlanComboBox.SelectedItem = plans.FirstOrDefault(item =>
                string.Equals(item, curveResult.SelectedPlan, StringComparison.OrdinalIgnoreCase));
            ApplyEmbeddedCurvePlan();
            StatusText.Text = result.WeeklyRows.Count == 0
                ? "没有可展示的历史周期"
                : $"已加载 {result.WeeklyRows.Count:N0}/{result.PeriodCount:N0} 个历史周期";
        }
        catch (OperationCanceledException)
        {
            if (IsLoaded)
            {
                StatusText.Text = "加载已取消";
            }
        }
        catch (Exception ex)
        {
            loadCancellation?.Cancel();
            if (!IsLoaded)
            {
                return;
            }

            StatusText.Text = "加载失败";
            System.Windows.MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            await ObserveBackgroundTasksAsync(estimateTask, curveTask);
        }
    }

    private static async Task ObserveBackgroundTasksAsync(
        Task<QuotaEstimateLoadResult>? estimateTask,
        Task<QuotaCostCurveResult>? curveTask)
    {
        if (estimateTask is not null)
        {
            try
            {
                await estimateTask;
            }
            catch
            {
                // The foreground load path reports the first failure. This await
                // observes any later exception so the parallel curve task cannot
                // become an unobserved fault after the window has handled an error.
            }
        }

        if (curveTask is not null)
        {
            try
            {
                await curveTask;
            }
            catch
            {
                // See the estimate task comment above.
            }
        }
    }

    private void CurvePlanComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyEmbeddedCurvePlan();
    }

    private void WeeklyGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loadedCurveResult is null || WeeklyGrid.SelectedItem is not QuotaWeeklyCycleRow selectedRow)
        {
            return;
        }

        var selectedCurve = loadedCurveResult.Curves.FirstOrDefault(item => item.PeriodStart == selectedRow.PeriodStart);
        if (selectedCurve is not null &&
            !string.Equals(CurvePlanComboBox.SelectedItem as string, selectedCurve.PlanName, StringComparison.OrdinalIgnoreCase))
        {
            CurvePlanComboBox.SelectedItem = selectedCurve.PlanName;
            return;
        }

        ApplyEmbeddedCurvePlan();
    }

    private void WeeklyGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindVisualParent<DataGridRow>(e.OriginalSource as DependencyObject) is not { } row)
        {
            return;
        }

        row.IsSelected = true;
        row.Focus();
    }

    private async void AnalyzeCycleMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (WeeklyGrid.SelectedItem is not QuotaWeeklyCycleRow selectedRow)
        {
            return;
        }

        var period = knownWeeklyPeriods.FirstOrDefault(item => item.PeriodStart == selectedRow.PeriodStart);
        if (period is null)
        {
            var cancellationToken = loadCancellation?.Token ?? CancellationToken.None;
            StatusText.Text = "正在定位所选周期...";
            var now = DateTimeOffset.UtcNow.ToOffset(CodexUsageReader.BeijingOffset);
            try
            {
                var periods = await Task.Run(
                    () => CodexQuotaCycleReader.ReadWeeklyCycles(currentQuota, now, cancellationToken),
                    cancellationToken);
                period = periods.FirstOrDefault(item => item.PeriodStart == selectedRow.PeriodStart);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        if (period is null || !IsLoaded)
        {
            if (IsLoaded)
            {
                StatusText.Text = "无法定位所选周期";
            }
            return;
        }

        var window = new QuotaCycleAnalysisWindow(period)
        {
            Owner = this
        };
        window.Show();
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

    private void ApplyEmbeddedCurvePlan()
    {
        if (loadedCurveResult is null || CurvePlanComboBox.SelectedItem is not string selectedPlan)
        {
            return;
        }

        var curves = loadedCurveResult.Curves
            .Where(item => string.Equals(item.PlanName, selectedPlan, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var selectedPeriodStart = (WeeklyGrid.SelectedItem as QuotaWeeklyCycleRow)?.PeriodStart;
        embeddedCurveControl.SetData(curves, selectedPeriodStart);
    }

    private void ApplyCurrentRows(IReadOnlyList<QuotaCurrentWindowRow> rows)
    {
        foreach (var row in rows)
        {
            if (string.Equals(row.Label, "5h", StringComparison.OrdinalIgnoreCase))
            {
                FiveHourValue.Text = row.RemainingText;
                FiveHourDetail.Text = row.DetailText;
                FiveHourDetail.ToolTip = row.CostDetail;
                FiveHourPlan.Text = row.PlanText;
                FiveHourStable.Text = row.StableText;
            }
            else
            {
                WeekValue.Text = row.RemainingText;
                WeekDetail.Text = row.DetailText;
                WeekDetail.ToolTip = row.CostDetail;
                WeekPlan.Text = row.PlanText;
                WeekStable.Text = row.StableText;
            }
        }
    }

    private void ApplyResetOpportunityPanel()
    {
        ResetOpportunityPanel.Children.Clear();
        var now = DateTimeOffset.UtcNow.ToOffset(CodexUsageReader.BeijingOffset);
        var summary = ResetOpportunityStore.Summarize(now);

        AddResetText(
            ResetOpportunityFormatter.FormatPanelTitle(summary),
            FontWeights.Bold,
            new SolidColorBrush(System.Windows.Media.Color.FromRgb(31, 41, 55)));

        if (summary.AvailableRecords.Count == 0)
        {
            AddResetText("无未过期重置卡");
            return;
        }

        foreach (var record in summary.AvailableRecords)
        {
            AddResetText(ResetOpportunityFormatter.FormatRecordLine(record, now));
        }
    }

    private void AddResetText(string text, FontWeight? fontWeight = null, System.Windows.Media.Brush? foreground = null)
    {
        ResetOpportunityPanel.Children.Add(new TextBlock
        {
            Text = text,
            FontWeight = fontWeight ?? FontWeights.Normal,
            Foreground = foreground ?? new SolidColorBrush(System.Windows.Media.Color.FromRgb(87, 99, 116)),
            Margin = new Thickness(0, 0, 0, 5),
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        });
    }

    private async void ManualEstimateButton_Click(object sender, RoutedEventArgs e)
    {
        ManualEstimateButton.IsEnabled = false;
        ManualResultText.Text = "正在估算...";
        var cancellationToken = loadCancellation?.Token ?? CancellationToken.None;

        try
        {
            var fromRemaining = ManualFromBox.Value ?? 90m;
            var toRemaining = ManualToBox.Value ?? 85m;
            var result = await Task.Run(
                () => QuotaEstimateCalculator.BuildManualWeekEstimate(
                    currentQuota,
                    fromRemaining,
                    toRemaining,
                    cancellationToken),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsLoaded)
            {
                return;
            }

            ManualResultText.Text = result;
        }
        catch (OperationCanceledException)
        {
            // Closing the window cancels the shared load token. Do not write
            // status text back into a window that is already being torn down.
        }
        catch (Exception ex)
        {
            if (IsLoaded)
            {
                ManualResultText.Text = $"估算失败：{ex.Message}";
            }
        }
        finally
        {
            if (IsLoaded)
            {
                ManualEstimateButton.IsEnabled = true;
            }
        }
    }

    private void QuotaCurveButton_Click(object sender, RoutedEventArgs e)
    {
        var window = new QuotaCostCurveWindow(currentQuota, knownWeeklyPeriods, usageReadGate)
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
