using System.Windows;

namespace CodexTokenMonitor;

public partial class QuotaCostCurveWindow : Window
{
    private readonly CodexQuotaEstimate currentQuota;
    private readonly IReadOnlyList<CodexQuotaCycle> knownPeriods;
    private readonly SemaphoreSlim? usageReadGate;
    private readonly QuotaCostCurveControl curveControl = new();
    private QuotaCostCurveResult? loadedResult;
    private CancellationTokenSource? loadCancellation;

    internal QuotaCostCurveWindow(
        CodexQuotaEstimate currentQuota,
        IReadOnlyList<CodexQuotaCycle> knownPeriods,
        SemaphoreSlim? usageReadGate = null)
    {
        this.currentQuota = currentQuota;
        this.knownPeriods = knownPeriods;
        this.usageReadGate = usageReadGate;
        InitializeComponent();
        CurveHost.Content = curveControl;
        Loaded += async (_, _) => await LoadAsync();
        Closed += (_, _) =>
        {
            loadCancellation?.Cancel();
            loadCancellation?.Dispose();
            loadCancellation = null;
        };
    }

    private async Task LoadAsync()
    {
        loadCancellation?.Cancel();
        loadCancellation?.Dispose();
        loadCancellation = new CancellationTokenSource();
        var token = loadCancellation.Token;
        try
        {
            StatusText.Text = "正在读取数据库...";
            PlanComboBox.IsEnabled = false;
            SetCurveState("正在准备费用曲线", "读取额度快照与已缓存的用量记录。", isLoading: true);
            var result = await Task.Run(
                () => QuotaCostCurveCalculator.Build(
                    currentQuota,
                    knownPeriods,
                    token,
                    usageReadGate),
                token);
            token.ThrowIfCancellationRequested();
            ApplyResult(result);
        }
        catch (OperationCanceledException)
        {
            if (IsLoaded)
            {
                StatusText.Text = "已取消";
                SetCurveState("已取消读取", "重新打开此窗口即可再次读取费用曲线。");
            }
        }
        catch (Exception ex)
        {
            if (!IsLoaded)
            {
                return;
            }

            StatusText.Text = "加载失败";
            SetCurveState("费用曲线加载失败", "暂时无法读取缓存数据。请稍后重新打开此窗口。");
            System.Windows.MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void PlanComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        ApplySelectedPlan();
    }

    private void ApplyResult(QuotaCostCurveResult result)
    {
        loadedResult = result;
        var plans = result.Curves
            .Select(item => item.PlanName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => string.Equals(item, result.SelectedPlan, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(item => item)
            .ToList();
        PlanComboBox.ItemsSource = plans;
        PlanComboBox.IsEnabled = plans.Count > 0;
        PlanComboBox.SelectedItem = plans.FirstOrDefault(item =>
            string.Equals(item, result.SelectedPlan, StringComparison.OrdinalIgnoreCase)) ?? plans.FirstOrDefault();
        ApplySelectedPlan();
    }

    private void ApplySelectedPlan()
    {
        if (loadedResult is null)
        {
            return;
        }

        if (PlanComboBox.SelectedItem is not string selectedPlan)
        {
            StatusText.Text = "暂无可用周期";
            SetCurveState("暂无可比较的周期", "需要额度快照和对应的用量记录。缓存完成后，重新打开此窗口查看。");
            return;
        }

        var curves = loadedResult.Curves
            .Where(item => string.Equals(item.PlanName, selectedPlan, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (!curves.Any(curve => curve.Points.Select(point => Math.Round(point.UsedPercent, 3)).Distinct().Take(2).Count() >= 2))
        {
            StatusText.Text = $"{selectedPlan} · 数据不足";
            SetCurveState("额度变化点还不够", "至少需要两个不同额度位置的记录，才能绘制费用曲线。继续使用并积累快照后再查看。");
            return;
        }

        curveControl.SetData(curves);
        CurveHost.Visibility = Visibility.Visible;
        CurveStatePanel.Visibility = Visibility.Collapsed;
        StatusText.Text = $"{selectedPlan} · {curves.Count:N0} 个周期 · {curves.Sum(item => item.Points.Count):N0} 个点";
    }

    private void SetCurveState(string title, string detail, bool isLoading = false)
    {
        CurveHost.Visibility = Visibility.Collapsed;
        CurveStatePanel.Visibility = Visibility.Visible;
        CurveStateTitle.Text = title;
        CurveStateDetail.Text = detail;
        CurveLoadingProgress.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
