using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using UserControl = System.Windows.Controls.UserControl;

namespace CodexTokenMonitor;

public partial class QuotaForecastPanel : UserControl
{
    private readonly DispatcherTimer ageTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private QuotaCycleAnalysisResult? analysis;
    private QuotaModelCapacityReport? capacities;
    private QuotaForecastResult? forecast;
    private bool ready;
    private bool changingSelection;

    public QuotaForecastPanel()
    {
        InitializeComponent();
        ageTimer.Tick += (_, _) => Recalculate();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) { Recalculate(); ageTimer.Start(); }
            else ageTimer.Stop();
        };
        Unloaded += (_, _) => ageTimer.Stop();
        ready = true;
    }

    internal event Action<QuotaTimelineForecastOverlay?>? ForecastChanged;
    internal event EventHandler? RefreshRequested;

    internal void SetData(QuotaCycleAnalysisResult result, QuotaModelCapacityReport report)
    {
        var firstLoad = analysis is null;
        analysis = result;
        capacities = report;
        if (firstLoad) TargetPicker.Value = result.Period.ResetAt.DateTime;
        AccountResetText.Text = $"账号重置 {result.Period.ResetAt:MM-dd HH:mm}";
        Recalculate();
    }

    internal void SetLoading(bool loading) => RefreshForecastButton.IsEnabled = !loading;

    private void ForecastInput_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ready) Recalculate();
    }

    private void TargetBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready) return;
        TargetPicker.Visibility = TargetBox.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        Recalculate();
    }

    private void TargetPicker_ValueChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (ready && TargetBox.SelectedIndex == 1) Recalculate();
    }

    private void Recalculate()
    {
        if (!ready || analysis is null || capacities is null) return;
        if (TargetBox.SelectedIndex == 1 && TargetPicker.Value is null)
        {
            forecast = null;
            changingSelection = true;
            ScenarioBox.ItemsSource = ScenarioGrid.ItemsSource = null;
            changingSelection = false;
            ForecastValueText.Text = "请填写目标时间";
            ForecastStatusText.Text = "手动时间仅用于预测";
            ForecastBasisText.Text = "时间统一按北京时间计算。";
            BudgetHintText.Visibility = Visibility.Collapsed;
            ForecastChanged?.Invoke(null);
            return;
        }

        var target = TargetBox.SelectedIndex == 1
            ? new DateTimeOffset(DateTime.SpecifyKind(TargetPicker.Value!.Value, DateTimeKind.Unspecified), CodexUsageReader.BeijingOffset)
            : analysis.Period.ResetAt;
        var lookback = LookbackBox.SelectedIndex switch
        {
            0 => TimeSpan.FromMinutes(15), 2 => TimeSpan.FromHours(3),
            3 => TimeSpan.MaxValue, _ => TimeSpan.FromHours(1)
        };
        var activity = ActivityBox.SelectedIndex switch { 1 => 75m, 2 => 50m, 3 => 25m, _ => 100m };
        forecast = QuotaForecastCalculator.Build(analysis, capacities, lookback, target, activity,
            priceCatalog: PriceSettingsStore.Current.CodexPresets);
        var selectedKey = (ScenarioBox.SelectedItem as QuotaForecastScenarioRow)?.Key;
        var relevantModels = analysis.Models.Select(item => ModelKey(item.ModelId))
            .Concat(capacities.Estimates.Select(item => ModelKey(item.ModelId)))
            .Concat(new[] { "gpt-6-astra", "gpt-5.6-sol", "gpt-5.6-terra", "gpt-5.6-luna" })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rows = new[] { QuotaForecastScenarioRow.From(forecast.Current, current: true) }
            .Concat(forecast.Models.Where(item => relevantModels.Contains(item.ModelId))
                .Select(item => QuotaForecastScenarioRow.From(item))).ToArray();
        changingSelection = true;
        ScenarioBox.ItemsSource = rows;
        ScenarioGrid.ItemsSource = rows;
        ScenarioBox.SelectedItem = rows.FirstOrDefault(item => item.Key == selectedKey) ?? rows[0];
        ScenarioGrid.SelectedItem = ScenarioBox.SelectedItem;
        changingSelection = false;
        var comparable = rows.Skip(1).Where(item => item.Scenario.Status == QuotaForecastStatus.Ready).ToArray();
        var closest = comparable.Where(item => item.Scenario.EnoughUntilReset == true)
            .MinBy(item => item.Scenario.RemainingAtResetPercent);
        BudgetHintText.Visibility = comparable.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        BudgetHintText.Text = closest is not null
            ? $"按额度预算，{closest.Label} 在可覆盖目标的方案中余量最小（预计 {closest.Remaining}）。"
            : "已标定的模型方案均预计提前用尽，可降低未来工作量后再比较。";
        ApplyScenario();
    }

    private void ScenarioBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || changingSelection) return;
        changingSelection = true;
        ScenarioGrid.SelectedItem = ScenarioBox.SelectedItem;
        changingSelection = false;
        ApplyScenario();
    }

    private void ScenarioGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || changingSelection || ScenarioGrid.SelectedItem is null) return;
        changingSelection = true;
        ScenarioBox.SelectedItem = ScenarioGrid.SelectedItem;
        changingSelection = false;
        ApplyScenario();
    }

    private void ApplyScenario()
    {
        if (forecast is null || ScenarioBox.SelectedItem is not QuotaForecastScenarioRow row) return;
        var scenario = row.Scenario;
        ForecastCaptionText.Text = row.Label + " · 预计用尽（不计重置）";
        ForecastValueText.Text = scenario.RunoutAtLocal is { } runout ? $"{runout:MM-dd HH:mm}" : "暂不可估";
        ForecastStatusText.Text = scenario.RemainingAtResetPercent is { } remaining
            ? scenario.EnoughUntilReset == true
                ? $"到目标 {forecast.TargetResetLocal:MM-dd HH:mm} 预计剩余 {remaining:N1}%"
                : $"早于目标用尽 · 目标 {forecast.TargetResetLocal:MM-dd HH:mm}"
            : scenario.Reason;
        if (scenario.MaxActivityPercent is { } budget && scenario.EnoughUntilReset == false)
            ForecastStatusText.Text += $"\n要覆盖目标，工作量需降至近期的 {budget:N1}% 以下。";
        var basis = forecast.LastSampleLocal is { } last
            ? $"截至 {last:MM-dd HH:mm:ss} · 参考 {QuotaConsumptionSpeedRow.FormatDuration(forecast.SampleDuration)}（含空闲）"
            : "没有可用的额度记录";
        if (scenario.RatePerHour is { } rate) basis += $"\n按 {rate:N2} 个百分点/小时，工作量 {forecast.ActivityPercent:N0}%。";
        ForecastBasisText.Text = basis + "\n" + scenario.Source + (scenario.CalibrationSource is not null ? $" · {scenario.SampleCount} 个标定分段" : "");
        ForecastChanged?.Invoke(scenario.RatePerHour is > 0m && forecast.LastSampleLocal is { } start &&
            forecast.CurrentRemainingPercent is { } current && forecast.TargetResetLocal > start
            ? new QuotaTimelineForecastOverlay(start, current, scenario.RatePerHour.Value, forecast.TargetResetLocal, row.Label)
            : null);
    }

    private void RefreshForecastButton_Click(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke(this, EventArgs.Empty);

    private static string ModelKey(string model) => System.Text.RegularExpressions.Regex.Replace(
        CodexModelCost.NormalizeModelId(model), @"-\d{4}-\d{2}-\d{2}$", "");
}

internal sealed record QuotaForecastScenarioRow(string Key, string Label, string Runout, string Remaining, QuotaForecastScenario Scenario)
{
    internal static QuotaForecastScenarioRow From(QuotaForecastScenario scenario, bool current = false)
    {
        var mode = scenario.Mode == "Fast"
            ? scenario.FastMultiplier is > 1m ? $"Fast {scenario.FastMultiplier:0.##}×" : "Fast"
            : "普通";
        var label = current ? "近期实测节奏" : $"{QuotaCycleModelPalette.ShortName(scenario.ModelId)} · {mode}";
        return new(current ? "observed" : scenario.ModelId + ":" + scenario.Mode,
            label, scenario.RunoutAtLocal is { } time ? time.ToString("MM-dd HH:mm") : "不可估",
            scenario.RemainingAtResetPercent is { } remaining ? $"{remaining:N1}%" : "—", scenario);
    }
}
