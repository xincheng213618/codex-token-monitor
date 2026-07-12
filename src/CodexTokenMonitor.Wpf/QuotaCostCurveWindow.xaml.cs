using System.Windows;

namespace CodexTokenMonitor;

public partial class QuotaCostCurveWindow : Window
{
    private readonly CodexQuotaEstimate currentQuota;
    private readonly IReadOnlyList<CodexQuotaCycle> knownPeriods;
    private readonly QuotaCostCurveControl curveControl = new();
    private QuotaCostCurveResult? loadedResult;
    private CancellationTokenSource? loadCancellation;

    internal QuotaCostCurveWindow(
        CodexQuotaEstimate currentQuota,
        IReadOnlyList<CodexQuotaCycle> knownPeriods)
    {
        this.currentQuota = currentQuota;
        this.knownPeriods = knownPeriods;
        InitializeComponent();
        CurveHost.Content = curveControl;
        Loaded += async (_, _) => await LoadAsync();
        Closed += (_, _) =>
        {
            loadCancellation?.Cancel();
            loadCancellation?.Dispose();
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
            var result = await Task.Run(
                () => QuotaCostCurveCalculator.Build(currentQuota, knownPeriods, token),
                token);
            token.ThrowIfCancellationRequested();
            loadedResult = result;
            var plans = result.Curves
                .Select(item => item.PlanName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(item => string.Equals(item, result.SelectedPlan, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(item => item)
                .ToList();
            PlanComboBox.ItemsSource = plans;
            PlanComboBox.SelectedItem = plans.FirstOrDefault(item =>
                string.Equals(item, result.SelectedPlan, StringComparison.OrdinalIgnoreCase));
            ApplySelectedPlan();
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "已取消";
        }
        catch (Exception ex)
        {
            StatusText.Text = "加载失败";
            System.Windows.MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void PlanComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        ApplySelectedPlan();
    }

    private void ApplySelectedPlan()
    {
        if (loadedResult is null || PlanComboBox.SelectedItem is not string selectedPlan)
        {
            return;
        }

        var curves = loadedResult.Curves
            .Where(item => string.Equals(item.PlanName, selectedPlan, StringComparison.OrdinalIgnoreCase))
            .ToList();
        curveControl.SetData(curves);
        StatusText.Text = $"{selectedPlan} · {curves.Count:N0} 个周期 · {curves.Sum(item => item.Points.Count):N0} 个点";
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
