using System.Windows;

namespace CodexTokenMonitor;

public partial class QuotaCostCurveWindow : Window
{
    private readonly CodexQuotaEstimate currentQuota;
    private readonly IReadOnlyList<CodexQuotaCycle> knownPeriods;
    private readonly AnalysisQuerySession querySession;
    private readonly QuotaCostCurveControl curveControl = new();
    private QuotaCostCurveResult? loadedResult;
    private string? loadFailureStatus;
    private string? loadFailureDetail;
    private bool isLoading;

    internal QuotaCostCurveWindow(
        CodexQuotaEstimate currentQuota,
        IReadOnlyList<CodexQuotaCycle> knownPeriods,
        MonitorRuntime? runtime = null)
    {
        this.currentQuota = currentQuota;
        this.knownPeriods = knownPeriods;
        querySession = new AnalysisQuerySession(runtime);
        InitializeComponent();
        CurveHost.Content = curveControl;
        Loaded += async (_, _) => await LoadAsync();
        Closed += (_, _) => querySession.Dispose();
    }

    private async Task LoadAsync()
    {
        if (isLoading || querySession.IsStopping) return;
        isLoading = true;
        try
        {
            SetStatus("正在读取数据库...");
            if (loadedResult is null)
            {
                PlanComboBox.IsEnabled = false;
                SetCurveState("正在准备费用曲线", "读取额度快照与已缓存的用量记录。", isLoading: true);
            }
            var result = await querySession.RunAsync(
                "额度费用曲线窗口加载",
                token => QuotaCostCurveCalculator.Build(
                    currentQuota,
                    knownPeriods,
                    token),
                requiresSharedIo: false);
            if (querySession.IsStopping || !IsLoaded) return;
            if (result.CacheWarnings.Count > 0)
            {
                loadFailureStatus = CacheFailureText.Summary(result.CacheWarnings, loadedResult is not null);
                loadFailureDetail = CacheFailureText.Detail(result.CacheWarnings);
                SetStatus(loadFailureStatus);
                if (loadedResult is null)
                    SetCurveState("费用曲线暂不可用", "恢复缓存后，重新打开此窗口重试。");
                return;
            }

            loadFailureStatus = null;
            loadFailureDetail = null;
            ApplyResult(result.Value);
        }
        catch (OperationCanceledException)
        {
            if (!querySession.IsStopping && IsLoaded)
            {
                SetStatus("已取消读取；重新打开此窗口可重试。");
                if (loadedResult is null)
                    SetCurveState("已取消读取", "重新打开此窗口即可再次读取费用曲线。");
            }
        }
        catch (Exception ex)
        {
            if (querySession.IsStopping || !IsLoaded)
            {
                return;
            }

            loadFailureStatus = "读取失败；" + (loadedResult is not null
                ? "当前显示上次成功结果，重新打开此窗口可重试。"
                : "费用曲线暂不可用，重新打开此窗口可重试。");
            loadFailureDetail = ex.ToString();
            SetStatus(loadFailureStatus);
            if (loadedResult is null)
                SetCurveState("费用曲线暂不可用", "暂时无法读取缓存数据。请稍后重新打开此窗口。");
        }
        finally
        {
            isLoading = false;
            if (!querySession.IsStopping && IsLoaded)
                CurveLoadingProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void SetStatus(string text)
    {
        StatusText.Text = loadFailureStatus ?? text;
        StatusText.ToolTip = loadFailureDetail;
    }

    private void PlanComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (querySession.IsStopping) return;
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
            SetStatus("暂无可用周期");
            SetCurveState("暂无可比较的周期", "需要额度快照和对应的用量记录。缓存完成后，重新打开此窗口查看。");
            return;
        }

        var curves = loadedResult.Curves
            .Where(item => string.Equals(item.PlanName, selectedPlan, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (!curves.Any(curve => curve.Points.Select(point => Math.Round(point.UsedPercent, 3)).Distinct().Take(2).Count() >= 2))
        {
            SetStatus($"{selectedPlan} · 数据不足");
            SetCurveState("额度变化点还不够", "至少需要两个不同额度位置的记录，才能绘制费用曲线。继续使用并积累快照后再查看。");
            return;
        }

        curveControl.SetData(curves);
        CurveHost.Visibility = Visibility.Visible;
        CurveStatePanel.Visibility = Visibility.Collapsed;
        SetStatus($"{selectedPlan} · {curves.Count:N0} 个周期 · {curves.Sum(item => item.Points.Count):N0} 个点");
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
