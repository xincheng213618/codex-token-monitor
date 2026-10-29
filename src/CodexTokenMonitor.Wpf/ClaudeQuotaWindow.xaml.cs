using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace CodexTokenMonitor;

/// <summary>
/// Manual /usage readings for Claude Code. Claude Code logs no quota, so the
/// user supplies the percentage and the window's spend comes from transcripts.
/// </summary>
internal partial class ClaudeQuotaWindow : Window
{
    /// <param name="fiveHourReset">The live 5h window's reset (official, entered or inferred).</param>
    /// <param name="weekReset">The live 7d window's reset when one is known.</param>
    public ClaudeQuotaWindow(DateTimeOffset? fiveHourReset, DateTimeOffset? weekReset = null)
    {
        InitializeComponent();
        var now = BeijingClock.Now;
        var readings = ClaudeQuotaReadingStore.Load();
        FiveHourResetPicker.Value = (fiveHourReset is { } end && end > now ? end : NextHour(now)).DateTime;
        WeekResetPicker.Value = (weekReset is { } week && week > now ? week : DefaultWeekReset(readings, now)).DateTime;
        ApplyReadings(readings);
    }

    /// <summary>True once a reading was added or removed, so the owner can refresh its panel.</summary>
    public bool Changed { get; private set; }

    private static DateTimeOffset NextHour(DateTimeOffset now) =>
        new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, now.Offset).AddHours(1);

    // The weekly reset keeps its weekday and time, so the last entered reset
    // rolled forward is almost always right.
    private static DateTimeOffset DefaultWeekReset(IReadOnlyList<ClaudeQuotaReading> readings, DateTimeOffset now)
    {
        var last = readings.Where(item => item.Kind == ClaudeQuotaWindowKind.Week).MaxBy(item => item.RecordedLocal);
        if (last is null)
        {
            return NextHour(now).AddDays(7);
        }

        var reset = last.ResetLocal.ToOffset(now.Offset);
        while (reset <= now)
        {
            reset = reset.AddDays(7);
        }

        return reset;
    }

    private async void FiveHourSaveButton_Click(object sender, RoutedEventArgs e) =>
        await SaveAsync(ClaudeQuotaWindowKind.FiveHour, FiveHourPercentBox, FiveHourResetPicker, FiveHourSaveButton);

    private async void WeekSaveButton_Click(object sender, RoutedEventArgs e) =>
        await SaveAsync(ClaudeQuotaWindowKind.Week, WeekPercentBox, WeekResetPicker, WeekSaveButton);

    private async Task SaveAsync(ClaudeQuotaWindowKind kind, TextBox percentBox, Xceed.Wpf.Toolkit.DateTimePicker resetPicker, Button button)
    {
        var text = percentBox.Text.Trim().TrimEnd('%');
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent) || percent <= 0 || percent > 100)
        {
            ShowValidation("已用百分比需要是 0–100 之间的数字（例如 /usage 显示的 23）。", percentBox);
            return;
        }

        var now = BeijingClock.Now;
        if (resetPicker.Value is not { } resetValue)
        {
            ShowValidation("请填写 /usage 显示的重置时间。", resetPicker);
            return;
        }

        var reset = new DateTimeOffset(resetValue, CodexUsageReader.BeijingOffset);
        var length = ClaudeQuotaEstimator.Length(kind);
        if (reset <= now || reset - now > length)
        {
            ShowValidation($"重置时间需要在现在之后的 {(kind == ClaudeQuotaWindowKind.FiveHour ? "5 小时" : "7 天")}内。", resetPicker);
            return;
        }

        button.IsEnabled = false;
        try
        {
            var reading = await Task.Run(() => ClaudeQuotaEstimator.ReadWindow(kind, percent, reset, now));
            if (reading.CostUsd <= 0)
            {
                ShowValidation("这个窗口内没有找到 Claude 模型的用量（MiMo 等网关模型不计入），无法估算。", percentBox);
                return;
            }

            ClaudeQuotaReadingStore.Add(reading);
            Changed = true;
            percentBox.Clear();
            ApplyReadings(ClaudeQuotaReadingStore.Load());
        }
        catch (Exception ex)
        {
            ShowValidation("读取日志失败：" + ex.Message, percentBox);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryGrid.SelectedItem is not ReadingRow row)
        {
            return;
        }

        ClaudeQuotaReadingStore.Remove(row.Reading);
        Changed = true;
        ApplyReadings(ClaudeQuotaReadingStore.Load());
    }

    private void HistoryGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        DeleteButton.IsEnabled = HistoryGrid.SelectedItem is ReadingRow;

    private void ApplyReadings(IReadOnlyList<ClaudeQuotaReading> readings)
    {
        ApplySummary(ClaudeQuotaEstimator.Summarize(readings, ClaudeQuotaWindowKind.FiveHour), FiveHourSummaryValue, FiveHourSummaryDetail);
        ApplySummary(ClaudeQuotaEstimator.Summarize(readings, ClaudeQuotaWindowKind.Week), WeekSummaryValue, WeekSummaryDetail);
        HistoryGrid.ItemsSource = readings.OrderByDescending(item => item.RecordedLocal).Select(item => new ReadingRow(item)).ToList();
    }

    private static void ApplySummary(ClaudeQuotaCapacity? capacity, TextBlock value, TextBlock detail)
    {
        if (capacity is null)
        {
            value.Text = "未录入";
            detail.Text = "记录一次读数后显示满额估算。";
            return;
        }

        value.Text = $"满额 ≈ {FormatUsd(capacity.CapacityUsd)}";
        var latest = capacity.Latest;
        var range = latest.CapacityUsdRange is { } r
            ? $"最近一次 {latest.UsedPercent:0.#}% → {FormatUsd(r.Low)}–{(r.High is { } high ? FormatUsd(high) : "?")}"
            : "";
        detail.Text = $"≈ {UsageDisplayFormatting.TokenAdaptive(capacity.CapacityTokens)} Token · {capacity.Windows} 个窗口的中位数 · {range}" +
                      (capacity.IsTentative ? $"\n已用都低于 {ClaudeQuotaEstimator.ReliablePercent:0}%，误差较大，用得多时再记一次。" : "");
    }

    internal static string FormatUsd(decimal value) =>
        value >= 10 ? $"${value:N0}" : $"${value:N2}";

    private void ShowValidation(string message, Control target)
    {
        MessageBox.Show(this, message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        target.Focus();
    }

    private sealed class ReadingRow(ClaudeQuotaReading reading)
    {
        public ClaudeQuotaReading Reading { get; } = reading;
        public string Recorded => Reading.RecordedLocal.ToString("MM-dd HH:mm");
        public string Window => $"{(Reading.Kind == ClaudeQuotaWindowKind.FiveHour ? "5h" : "7d")} · 重置 {Reading.ResetLocal:MM-dd HH:mm}" +
                                (Reading.Source == ClaudeQuotaReadingSource.Official ? " · 官方" : "");
        public string Percent => $"{Reading.UsedPercent:0.#}%";
        public string Cost => FormatUsd(Reading.CostUsd);
        public string Tokens => UsageDisplayFormatting.TokenAdaptive(Reading.Tokens);
        public string Capacity => Reading.CapacityUsdRange is { } range
            ? $"{FormatUsd(Reading.CapacityUsd!.Value)}（{FormatUsd(range.Low)}–{(range.High is { } high ? FormatUsd(high) : "?")}）"
            : "-";
        public string CapacityTokens => Reading.CapacityTokens is { } tokens ? UsageDisplayFormatting.TokenAdaptive(tokens) : "-";
    }
}
