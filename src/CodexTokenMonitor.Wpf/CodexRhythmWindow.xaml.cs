using System.Globalization;
using System.Windows;
using ScottPlot.TickGenerators;
using PlotColor = ScottPlot.Color;

namespace CodexTokenMonitor;

public partial class CodexRhythmWindow : Window
{
    private readonly Func<Task> refresh;
    private CodexMessageHistory? current;
    private TimeSpan currentOffset;

    internal CodexRhythmWindow(Func<Task> refresh)
    {
        this.refresh = refresh;
        InitializeComponent();
        SilencePlot.UserInputProcessor.Disable();
        SilencePlot.Visibility = Visibility.Collapsed;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await refresh();

    internal void ShowRefreshStatus(bool refreshing, string? error)
    {
        RefreshButton.IsEnabled = !refreshing;
        SourceText.ToolTip = error;
        var status = refreshing ? "正在更新…" : error is not null ? "更新失败，可重试" : "每小时更新";
        SourceText.Text = current is null ? status :
            $"{current.Source} · {DayTimeZoneSettings.Format(currentOffset)} · 更新于 {current.UpdatedAt.ToOffset(currentOffset):yyyy-MM-dd HH:mm:ss} · {status}";
    }

    internal void ShowHistory(CodexMessageHistory history, TimeSpan reportingOffset)
    {
        var previous = current;
        var previousOffset = currentOffset;
        current = history;
        currentOffset = reportingOffset;
        ShowRefreshStatus(!RefreshButton.IsEnabled, SourceText.ToolTip as string);
        if (previous is not null && previousOffset == reportingOffset && previous.Skipped == history.Skipped &&
            previous.Messages.AsSpan().SequenceEqual(history.Messages) &&
            previous.TurnStarts.AsSpan().SequenceEqual(history.TurnStarts))
            return;

        var rhythm = CodexUsageRhythm.Calculate(history, reportingOffset);
        var validDays = rhythm.Days.Count(day => day.Messages.HasValue);
        CoverageText.Text = rhythm.Days.Length == 0
            ? history.Messages.Length == 0 ? "未找到人工消息记录。" : $"已读取 {history.Messages.Length:N0} 次交互，完整自然日不足。"
            : $"{rhythm.Days[0].Date:yyyy-MM-dd} 至 {rhythm.Days[^1].Date:yyyy-MM-dd} · " +
              $"{validDays:N0} 个有效日 · {rhythm.Days.Sum(day => day.Messages ?? 0):N0} 次交互";
        if (rhythm.Days.Length > validDays)
            CoverageText.Text += $" · {rhythm.Days.Length - validDays:N0} 天覆盖待确认";
        if (history.Skipped > 0)
            CoverageText.Text += $" · 跳过 {history.Skipped:N0} 项无法核验的记录";
        var samples = rhythm.SilenceSamples;
        SilenceText.Text = samples == 0 ? "暂无可计算的最长静默样本。" :
            $"最长静默 · {samples:N0} 个有效样本 · 中位 {rhythm.MedianSilenceHours:F1} 小时 · " +
            $"短于 6 小时 {rhythm.SilencesUnderSix:N0} 次（{(double)rhythm.SilencesUnderSix / samples:P1}） · " +
            $"短于 5 小时 {rhythm.SilencesUnderFive:N0} 次（{(double)rhythm.SilencesUnderFive / samples:P1}）";
        MonthGrid.ItemsSource = rhythm.Months;
        DayGrid.ItemsSource = rhythm.Days.Reverse().ToArray();
        WeekGrid.ItemsSource = rhythm.Weeks.Reverse().Select(week => new
        {
            week.Monday,
            Window = week.WindowStart == week.WindowEnd ? $"{week.WindowStart:hh\\:mm}"
                : $"{week.WindowStart:hh\\:mm}–{(week.WindowEnd < week.WindowStart ? "次日 " : "")}{week.WindowEnd:hh\\:mm}",
            Coverage = $"{week.WindowCount} / {week.Samples}"
        }).ToArray();
        LongGapsGrid.ItemsSource = rhythm.LongSilences;
        LongGapsTab.Header = $"超过 24 小时（{rhythm.LongSilences.Length}）";
        DrawSilencePlot(rhythm);
    }

    private void DrawSilencePlot(CodexUsageRhythm rhythm)
    {
        var plot = SilencePlot.Plot;
        plot.Clear();
        var silences = rhythm.Days.Select(day => day.LongestSilence).OfType<CodexSilence>().ToArray();
        SilencePlot.Visibility = silences.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (silences.Length == 0) return;

        var dates = silences.Select(gap => gap.Start.Date.ToOADate()).ToArray();
        var hours = silences.Select(gap => gap.Start.TimeOfDay.TotalHours).ToArray();
        var points = plot.Add.Scatter(dates, hours, PlotColor.FromHex("#147D70"));
        points.LineWidth = 0;
        points.MarkerSize = 5;
        var first = rhythm.Days[0].Date.ToDateTime(TimeOnly.MinValue).ToOADate();
        var last = rhythm.Days[^1].Date.ToDateTime(TimeOnly.MinValue).ToOADate();
        var days = (int)(last - first) + 1;
        var step = Math.Max(1, (days + 6) / 7);
        var dateTicks = Enumerable.Range(0, (days - 1) / step + 1).Select(index => first + index * step).ToArray();
        var dateFormat = days >= 365 ? "yyyy-MM" : "MM-dd";
        plot.Axes.Bottom.TickGenerator = new NumericManual(dateTicks,
            dateTicks.Select(tick => DateTime.FromOADate(tick).ToString(dateFormat, CultureInfo.InvariantCulture)).ToArray());
        var clockTicks = Enumerable.Range(0, 9).Select(index => index * 3d).ToArray();
        plot.Axes.Left.TickGenerator = new NumericManual(clockTicks, clockTicks.Select(hour => $"{hour:00}:00").ToArray());
        plot.Grid.MajorLineColor = PlotColor.FromHex("#E2E8EE");
        plot.Axes.SetLimits(first - 1, last + 1, -0.25, 24.25);
        SilencePlot.Refresh();
    }
}
