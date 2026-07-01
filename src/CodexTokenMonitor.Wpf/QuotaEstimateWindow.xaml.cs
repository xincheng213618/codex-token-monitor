using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CodexTokenMonitor;

public partial class QuotaEstimateWindow : Window
{
    private readonly CodexQuotaEstimate currentQuota;
    private readonly IReadOnlyList<CodexQuotaCycle> knownWeeklyPeriods;
    private CancellationTokenSource? loadCancellation;

    internal QuotaEstimateWindow(CodexQuotaEstimate currentQuota, IReadOnlyList<CodexQuotaCycle>? knownWeeklyPeriods = null)
    {
        this.currentQuota = currentQuota;
        this.knownWeeklyPeriods = knownWeeklyPeriods ?? Array.Empty<CodexQuotaCycle>();
        InitializeComponent();
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

        try
        {
            StatusText.Text = "正在加载估算...";
            WeeklyGrid.ItemsSource = null;

            var now = DateTimeOffset.UtcNow.ToOffset(CodexUsageReader.BeijingOffset);
            var result = await Task.Run(
                () => QuotaEstimateCalculator.BuildLoadResult(
                    currentQuota,
                    now,
                    knownWeeklyPeriods,
                    cancellationToken),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            ApplyCurrentRows(result.CurrentRows);
            WeeklyGrid.ItemsSource = result.WeeklyRows;
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
            StatusText.Text = "加载失败";
            System.Windows.MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ApplyCurrentRows(IReadOnlyList<QuotaCurrentWindowRow> rows)
    {
        foreach (var row in rows)
        {
            if (string.Equals(row.Label, "5h", StringComparison.OrdinalIgnoreCase))
            {
                FiveHourValue.Text = row.RemainingText;
                FiveHourDetail.Text = row.DetailText;
                FiveHourPlan.Text = row.PlanText;
                FiveHourStable.Text = row.StableText;
            }
            else
            {
                WeekValue.Text = row.RemainingText;
                WeekDetail.Text = row.DetailText;
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
            Margin = new Thickness(0, 0, 18, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
    }

    private async void ManualEstimateButton_Click(object sender, RoutedEventArgs e)
    {
        ManualEstimateButton.IsEnabled = false;
        ManualResultText.Text = "正在估算...";

        try
        {
            var fromRemaining = ManualFromBox.Value ?? 90m;
            var toRemaining = ManualToBox.Value ?? 85m;
            var result = await Task.Run(
                () => QuotaEstimateCalculator.BuildManualWeekEstimate(
                    currentQuota,
                    fromRemaining,
                    toRemaining));
            ManualResultText.Text = result;
        }
        catch (Exception ex)
        {
            ManualResultText.Text = $"估算失败：{ex.Message}";
        }
        finally
        {
            ManualEstimateButton.IsEnabled = true;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
