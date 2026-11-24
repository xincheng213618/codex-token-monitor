using System.Globalization;
using System.Windows;
using ScottPlot.TickGenerators;
using ScottPlot.WPF;
using PlotColor = ScottPlot.Color;

namespace CodexTokenMonitor;

public partial class CodexRhythmWindow : Window
{
    private readonly Func<Task> refresh;
    private CodexMessageHistory? current;
    private TimeSpan currentOffset;
    private string? refreshError;
    private CodexRhythmDay[] rhythmDays = [];
    private IReadOnlyDictionary<DateOnly, long> tokensByDay = new Dictionary<DateOnly, long>();
    internal UsageSource RhythmSource { get; private set; } = UsageSource.Codex;

    internal CodexRhythmWindow(Func<Task> refresh, UsageSource source = UsageSource.Codex)
    {
        this.refresh = refresh;
        InitializeComponent();
        SetRhythmSource(source);
        SilencePlot.UserInputProcessor.Disable();
        InteractionRatePlot.UserInputProcessor.Disable();
        WindowTokenPlot.UserInputProcessor.Disable();
        SilencePlot.Visibility = Visibility.Collapsed;
        InitializeDailyTrends();
    }

    internal void SetRhythmSource(UsageSource source)
    {
        var changed = RhythmSource != source;
        RhythmSource = source;
        var label = $"{UsageSourceRegistry.For(source).Title} 使用节律";
        Title = label;
        RhythmHeaderTitle.Text = label;
        if (changed)
        {
            current = null;
            ShowAnalysis(null, new Dictionary<DateOnly, long>());
            ShowTodaySnapshot(null);
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await refresh();

    internal void ShowRefreshStatus(bool refreshing, string? error)
    {
        RefreshButton.IsEnabled = !refreshing;
        RefreshButton.Content = refreshing ? "正在更新…" : error is null ? "更新统计" : "重试更新";
        refreshError = error;
        UpdateRefreshTooltip();
    }

    private void UpdateRefreshTooltip()
    {
        var source = current is { } history
            ? $"{history.Source} · {DayTimeZoneSettings.Format(currentOffset)} · 更新于 {history.UpdatedAt.ToOffset(currentOffset):yyyy-MM-dd HH:mm:ss}"
            : "等待首次更新";
        var todayText = todaySnapshot is { TotalTokens: { } total } today
            ? $"\n截至 {today.Cutoff:HH:mm:ss} · 今日 Token {CodexRhythmTokenFormat.Format(total)}" : "";
        RefreshButton.ToolTip = source + todayText + (refreshError is { } error ? $"\n更新失败 · {error}" : "\n每小时更新");
    }

    internal void ShowHistory(CodexMessageHistory history, TimeSpan reportingOffset)
    {
        var previous = current;
        var previousOffset = currentOffset;
        current = history;
        currentOffset = reportingOffset;
        if (previous is not null && previousOffset != reportingOffset)
        {
            ShowAnalysis(null, new Dictionary<DateOnly, long>());
            ShowAnalysisStatus("正在统计当前时区的交互和 Token…");
            ShowTodaySnapshot(null);
        }
        UpdateRefreshTooltip();
        if (previous is not null && previousOffset == reportingOffset && previous.Skipped == history.Skipped &&
            previous.Messages.AsSpan().SequenceEqual(history.Messages) &&
            previous.TurnStarts.AsSpan().SequenceEqual(history.TurnStarts))
            return;

        var rhythm = CodexUsageRhythm.Calculate(history, reportingOffset);
        MonthGrid.ItemsSource = rhythm.Months;
        rhythmDays = rhythm.Days.Reverse().ToArray();
        UpdateDayRows();
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

    internal void ShowAnalysisStatus(string message)
    {
        AnalysisStatusText.Text = message;
        AnalysisStatusText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    internal void ShowAnalysis(CodexRhythmWindowAnalysis? analysis, IReadOnlyDictionary<DateOnly, long> dailyTokens)
    {
        tokensByDay = dailyTokens;
        UpdateDayRows();
        if (analysis is null || analysis.Points.Length == 0)
        {
            AnalysisContent.Visibility = Visibility.Collapsed;
            AnalysisGrid.ItemsSource = null;
            InteractionRatePlot.Plot.Clear();
            WindowTokenPlot.Plot.Clear();
            ShowAnalysisStatus("暂无完整自然日可分析。");
            return;
        }

        ShowAnalysisStatus("");
        AnalysisContent.Visibility = Visibility.Visible;
        var points = analysis.Points.OrderBy(point => point.Hours).ToArray();
        AnalysisRangeText.Text = $"{analysis.Start:yyyy-MM-dd} 至 {analysis.End:yyyy-MM-dd} · {DayTimeZoneSettings.Format(currentOffset)}";
        AnalysisInsightText.Text = DescribeInteractionRate(points);
        AnalysisGrid.ItemsSource = points;

        var hours = points.Select(point => (double)point.Hours).ToArray();
        InteractionRateEmptyText.Visibility = DrawAnalysisPlot(InteractionRatePlot, hours,
            points.Select(point => point.MedianMessagesPerHour ?? double.NaN).ToArray(), "#147D70")
            ? Visibility.Collapsed : Visibility.Visible;
        WindowTokenEmptyText.Visibility = DrawAnalysisPlot(WindowTokenPlot, hours,
            points.Select(point => point.MedianTokens ?? double.NaN).ToArray(), "#3264AA", compactTokens: true)
            ? Visibility.Collapsed : Visibility.Visible;
    }

    private static string DescribeInteractionRate(CodexRhythmWindowPoint[] points)
    {
        var peak = points.FirstOrDefault(point => point.Hours == 1)?.MedianMessagesPerHour;
        var halfDay = points.FirstOrDefault(point => point.Hours == 12)?.MedianMessagesPerHour;
        var fullDay = points.FirstOrDefault(point => point.Hours == 24)?.MedianMessagesPerHour;
        if (peak is not { } oneHour || halfDay is not { } twelveHours || fullDay is not { } allHours)
            return "样本不足，暂时无法比较 1、12、24 小时的交互速度。";
        if (oneHour > twelveHours && oneHour > allHours)
            return $"以最密集 1 小时的 {oneHour:0.##} 次/小时为基准，12 小时降至 {twelveHours:0.##}（−{1 - twelveHours / oneHour:P0}），全天降至 {allHours:0.##}（−{1 - allHours / oneHour:P0}）。";
        return $"1 小时、12 小时和全天的交互速度中位数分别为 {oneHour:0.##}、{twelveHours:0.##} 和 {allHours:0.##} 次/小时。";
    }

    private static bool DrawAnalysisPlot(
        WpfPlot control, double[] hours, double[] values, string color, bool compactTokens = false)
    {
        var plot = control.Plot;
        plot.Clear();
        if (values.All(double.IsNaN))
        {
            control.Visibility = Visibility.Collapsed;
            return false;
        }

        control.Visibility = Visibility.Visible;
        var curve = plot.Add.Scatter(hours, values, PlotColor.FromHex(color));
        curve.LineWidth = 2;
        curve.MarkerSize = 6;
        plot.Axes.Bottom.TickGenerator = new NumericManual(hours, hours.Select(hour => hour.ToString("0")).ToArray());
        plot.Axes.Left.MinimumSize = compactTokens ? 57 : 44;
        plot.Axes.Left.TickGenerator = new NumericAutomatic
        {
            LabelFormatter = value => compactTokens ? CodexRhythmTokenFormat.Format(value) : value.ToString("0.##")
        };
        plot.Grid.MajorLineColor = PlotColor.FromHex("#E2E8EE");
        var maximum = Math.Max(1, values.Where(double.IsFinite).Max());
        plot.Axes.SetLimits(0, hours[^1] + 1, -0.02 * maximum, 1.12 * maximum);
        control.Refresh();
        return true;
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
