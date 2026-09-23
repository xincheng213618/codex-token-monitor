using System.Windows;
using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;

namespace CodexTokenMonitor;

public partial class QuotaConsumptionTimelineView : UserControl
{
    private readonly QuotaConsumptionTimelineChart chart = new();
    private QuotaCycleAnalysisResult? sourceAnalysis;
    private QuotaCycleAnalysisResult? analysis;
    private IReadOnlyList<QuotaConsumptionSpeedRow> speedRows = Array.Empty<QuotaConsumptionSpeedRow>();
    private int? selectedBandIndex;
    private bool ready;
    private QuotaTimelineForecastOverlay? forecast;
    private bool forecastRangeSelected;
    private bool resumeForecastRange;
    private int previousForecastRange = 5;

    public QuotaConsumptionTimelineView()
    {
        InitializeComponent();
        TimelineHost.Content = chart;
        chart.BandSelected += Chart_BandSelected;
    }

    internal event EventHandler<QuotaCycleAnalysisBand>? BandSelected;

    internal void SetData(QuotaCycleAnalysisResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        // Visibility switches reuse the same projection, selection and time range.
        if (ReferenceEquals(sourceAnalysis, result)) return;

        var previousRange = sourceAnalysis is null ? 0 : RangeBox.SelectedIndex;
        var projected = result with { Bands = QuotaConsumptionTimelineCalculator.BuildBands(result) };
        sourceAnalysis = result;
        analysis = projected;
        speedRows = projected.Bands.Select(QuotaConsumptionSpeedRow.From).ToArray();
        ready = false;
        chart.SetData(projected);
        ApplySummary();
        RangeBox.IsEnabled = projected.Timeline.Count > 0;
        RangeBox.SelectedIndex = previousRange;
        ready = true;
        ApplyVisibleRange();
        SelectBand(selectedBandIndex);
    }

    internal void SetForecast(QuotaTimelineForecastOverlay? overlay)
    {
        forecast = overlay;
        chart.SetForecast(overlay);
        ((ComboBoxItem)RangeBox.Items[4]).IsEnabled = overlay is not null;
        ((ComboBoxItem)RangeBox.Items[5]).IsEnabled = overlay is not null;
        if (overlay is not null && (!forecastRangeSelected || resumeForecastRange))
        {
            forecastRangeSelected = true;
            resumeForecastRange = false;
            RangeBox.SelectedIndex = previousForecastRange;
        }
        else if (overlay is null && RangeBox.SelectedIndex >= 4)
        {
            previousForecastRange = RangeBox.SelectedIndex;
            RangeBox.SelectedIndex = 0;
            resumeForecastRange = true;
        }
        if (ready && RangeBox.SelectedIndex >= 4) ApplyVisibleRange();
    }

    internal void SelectBand(int? bandIndex)
    {
        if (analysis is null)
        {
            selectedBandIndex = bandIndex;
            return;
        }

        var row = speedRows.FirstOrDefault(item => item.Band.BandIndex == bandIndex);
        selectedBandIndex = row?.Band.BandIndex;
        chart.SelectedBandIndex = selectedBandIndex;
        FocusBandButton.IsEnabled = row is not null && row.Elapsed > TimeSpan.Zero && analysis.Timeline.Count > 0;
        if (ready && RangeBox.SelectedIndex == 3) ApplyVisibleRange();
    }

    private void Chart_BandSelected(object? sender, QuotaCycleAnalysisBand band)
    {
        SelectBand(band.BandIndex);
        // Only user selection is sent back to the parent; SelectBand never echoes it.
        BandSelected?.Invoke(this, band);
    }

    private void ApplySummary()
    {
        var points = analysis!.Timeline;
        SummaryText.Text = points.Count > 0
            ? $"最后剩余 {100m - points[^1].UsedPercent:N1}% · {points[^1].TimestampLocal:MM-dd HH:mm:ss}"
            : "等待可对齐的额度记录";

        var bandSize = analysis.BandSizePercent;
        var fastest = speedRows
            .Where(item => Math.Abs(item.Band.QuotaDropPercent - bandSize) <= 0.001m &&
                           item.RateValue is > 0m)
            .MinBy(item => item.Elapsed);
        FastestSummaryText.Text = fastest is null
            ? $"最快完整 {bandSize:N0}%：样本不足"
            : $"最快完整 {bandSize:N0}%：{fastest.Duration} · 包含空闲";
        FastestSummaryText.ToolTip = fastest is null
            ? $"形成完整的 {bandSize:N0} 个百分点消耗分段后显示；分段边界按额度时间线插值。"
            : $"{fastest.QuotaRange} · {fastest.Model}\n{fastest.TimeRange}\n段平均 {fastest.Rate} 个百分点/小时，包含空闲。";
    }

    private void RangeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ready && forecast is null) resumeForecastRange = false;
        if (ready) ApplyVisibleRange();
    }

    private void ApplyVisibleRange()
    {
        if (analysis is null || analysis.Timeline.Count == 0)
        {
            RangeText.Text = "等待可对齐的额度时间点";
            return;
        }

        if (RangeBox.SelectedIndex == 3)
        {
            if (TryShowSelectedBand()) return;
            RangeBox.SelectedIndex = 0;
            return;
        }

        var first = analysis.Timeline[0].TimestampLocal;
        var last = analysis.Timeline[^1].TimestampLocal;
        var start = first;
        var end = last;
        var label = "已有记录";
        switch (RangeBox.SelectedIndex)
        {
            case 1:
                start = last - first > TimeSpan.FromHours(1) ? last.AddHours(-1) : first;
                label = "最近 1 小时 · 截至最后记录";
                break;
            case 2:
                start = analysis.Period.PeriodStart < first ? analysis.Period.PeriodStart : first;
                var reset = analysis.Period.ResetAt > analysis.Period.PeriodEnd ? analysis.Period.ResetAt : analysis.Period.PeriodEnd;
                end = reset > last ? reset : last;
                label = "完整周期 · 无记录时段留空";
                break;
            case 4 when forecast is not null:
                start = last - first > TimeSpan.FromHours(3) ? last.AddHours(-3) : first;
                end = forecast.TargetResetLocal;
                label = "实测 + 虚线预测 · 截至目标时间";
                break;
            case 5 when forecast is not null:
                start = last - first > TimeSpan.FromHours(3) ? last.AddHours(-3) : first;
                var targetTicks = (forecast.TargetResetLocal - forecast.StartLocal).Ticks;
                var hoursToTarget = targetTicks / (decimal)TimeSpan.TicksPerHour;
                var forecastTicks = forecast.RatePerHour * hoursToTarget <= forecast.RemainingPercent
                    ? targetTicks
                    : Math.Min(targetTicks, Math.Max(TimeSpan.TicksPerHour,
                        forecast.RemainingPercent / forecast.RatePerHour * TimeSpan.TicksPerHour + TimeSpan.FromMinutes(15).Ticks));
                end = forecast.StartLocal.AddTicks((long)forecastTicks);
                label = end < forecast.TargetResetLocal
                    ? $"实测 + 近期预测 · 目标 {forecast.TargetResetLocal:MM-dd HH:mm} 在右侧范围之外"
                    : "实测 + 近期预测 · 已覆盖目标时间";
                break;
        }
        ShowRange(start, end, label);
    }

    private bool TryShowSelectedBand()
    {
        var row = speedRows.FirstOrDefault(item => item.Band.BandIndex == selectedBandIndex);
        if (analysis is null || analysis.Timeline.Count == 0 || row is null || row.Elapsed <= TimeSpan.Zero) return false;

        var points = analysis.Timeline;
        var padding = TimeSpan.FromTicks(Math.Max(TimeSpan.FromSeconds(15).Ticks, row.Elapsed.Ticks / 8));
        var start = row.Band.StartLocal - points[0].TimestampLocal > padding ? row.Band.StartLocal - padding : points[0].TimestampLocal;
        var end = points[^1].TimestampLocal - row.Band.EndLocal > padding ? row.Band.EndLocal + padding : points[^1].TimestampLocal;
        ShowRange(start, end, $"所选 {row.QuotaRange} · {row.Duration}");
        return true;
    }

    private void ShowRange(DateTimeOffset start, DateTimeOffset end, string label)
    {
        if (end <= start) end = start.AddMinutes(1);
        if (chart.ViewStart != start || chart.ViewEnd != end) chart.SetVisibleRange(start, end);
        RangeText.Text = $"{label} · {start:MM-dd HH:mm:ss} → {end:MM-dd HH:mm:ss} · 北京时间";
    }

    private void FocusBandButton_Click(object sender, RoutedEventArgs e)
    {
        if (!FocusBandButton.IsEnabled) return;
        if (RangeBox.SelectedIndex != 3) RangeBox.SelectedIndex = 3;
        else ApplyVisibleRange();
    }
}
