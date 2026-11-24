using System.Globalization;
using System.Windows;
using System.Windows.Input;
using ScottPlot.Plottables;
using ScottPlot.TickGenerators;
using ScottPlot.WPF;
using PlotColor = ScottPlot.Color;

namespace CodexTokenMonitor;

public partial class CodexRhythmWindow
{
    private CodexRhythmDailyUsage[] dailyUsage = [];
    private CodexRhythmToday? todaySnapshot;

    private void InitializeDailyTrends()
    {
        DailyOverlayPlot.UserInputProcessor.Disable();
        DailyOverlayPlot.ToolTip = "点击日期可定位到下方明细。";
        DailyOverlayPlot.PreviewMouseLeftButtonDown += DailyTrend_DateClicked;
        DailyTrendRange.SelectionChanged += (_, _) => DrawDailyTrends();
        DailyTrendMode.SelectionChanged += (_, _) => DrawDailyTrends();
        DayGrid.SelectionChanged += (_, _) => MarkSelectedDay();
        DailyDetailPanel.SizeChanged += (_, args) =>
        {
            // Reserve space for the table when the window is short.
            DailyChart.Height = Math.Clamp(args.NewSize.Height - 205, 100, 205);
        };
    }

    internal void ShowTodaySnapshot(CodexRhythmToday? today)
    {
        todaySnapshot = today;
        UpdateDayRows();
        UpdateRefreshTooltip();
    }

    private void UpdateDayRows()
    {
        var selectedDate = (DayGrid.SelectedItem as CodexRhythmDailyUsage)?.Date;
        dailyUsage = CodexRhythmDailyUsage.Create(rhythmDays, tokensByDay);
        if (todaySnapshot is { } today)
        {
            dailyUsage = dailyUsage.Where(row => row.Date != today.Day.Date)
                .Append(new CodexRhythmDailyUsage(today.Day, today.TotalTokens, today.Cutoff))
                .OrderByDescending(row => row.Date).ToArray();
        }
        DayGrid.ItemsSource = dailyUsage;
        if (selectedDate is { } date)
            DayGrid.SelectedItem = dailyUsage.FirstOrDefault(day => day.Date == date);
        DrawDailyTrends();
    }

    private void DrawDailyTrends()
    {
        DailyTrends.Visibility = dailyUsage.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (dailyUsage.Length == 0) return;

        var limit = DailyTrendRange.SelectedIndex switch
        {
            0 => 28,
            1 => CodexRhythmWindowAnalysis.LookbackDays,
            _ => dailyUsage.Length
        };
        var rolling = DailyTrendMode.SelectedIndex == 1;
        var history = dailyUsage.Where(day => !rolling || !day.SnapshotThrough.HasValue).Reverse().ToArray();
        // Use dates preceding the visible range when the first points need a complete seven-day window.
        var days = CodexRhythmDailyTrendPoint.Create(history, rolling).TakeLast(limit).ToArray();
        var partialDate = rolling ? null : todaySnapshot?.Day.Date;
        DailyTrendScaleText.Text = !rolling && todaySnapshot is { } today
            ? $"走势指数 · 基准 100 · 今日截至 {today.Cutoff:HH:mm:ss}"
            : "走势指数 · 各指标均值为 100";
        var dates = days.Select(day => day.Date.ToDateTime(TimeOnly.MinValue).ToOADate()).ToArray();
        var lines = new[]
        {
            (Values: IndexToMean(days, partialDate, day => day.Messages), Color: "#147D70", Marker: ScottPlot.MarkerShape.FilledCircle),
            (Values: IndexToMean(days, partialDate, day => day.Tokens), Color: "#3264AA", Marker: ScottPlot.MarkerShape.FilledDiamond),
            (Values: IndexToMean(days, partialDate, day => day.TokensPerMessage), Color: "#93612C", Marker: ScottPlot.MarkerShape.FilledSquare)
        };
        var maximum = lines.SelectMany(line => line.Values).Where(double.IsFinite).DefaultIfEmpty(double.NaN).Max();
        var plot = DailyOverlayPlot.Plot;
        plot.Clear();
        var hasSamples = double.IsFinite(maximum);
        DailyOverlayPlot.Visibility = hasSamples ? Visibility.Visible : Visibility.Collapsed;
        DailyTrendEmpty.Visibility = hasSamples ? Visibility.Collapsed : Visibility.Visible;
        if (!hasSamples)
        {
            DailyTrendEmpty.Text = rolling ? "暂无连续 7 个自然日的可用样本" : "暂无趋势样本";
            return;
        }

        plot.Add.HorizontalLine(100, 1, PlotColor.FromHex("#A7B5BE"));
        foreach (var line in lines)
        {
            var curve = plot.Add.Scatter(dates, line.Values, PlotColor.FromHex(line.Color));
            curve.LineWidth = 1.7f;
            curve.MarkerShape = line.Marker;
            curve.MarkerSize = dates.Length <= 56 ? 4 : 2;
        }
        plot.Add.VerticalLine(0, 1, PlotColor.FromHex("#73828F")).IsVisible = false;
        var labelCount = Math.Min(7, dates.Length);
        var labels = labelCount == 1 ? dates : Enumerable.Range(0, labelCount)
            .Select(index => dates[index * (dates.Length - 1) / (labelCount - 1)]).ToArray();
        var dateFormat = dates[^1] - dates[0] >= 365 ? "yyyy-MM" : "MM-dd";
        var tickText = labels.Select(date => DateTime.FromOADate(date)
            .ToString(dateFormat, CultureInfo.InvariantCulture)).ToArray();

        plot.Axes.Bottom.TickGenerator = new NumericManual(labels, tickText);
        plot.Axes.Left.MinimumSize = 39;
        plot.Axes.Left.TickGenerator = new NumericAutomatic { LabelFormatter = value => value.ToString("0") };
        plot.Grid.MajorLineColor = PlotColor.FromHex("#E2E8EE");
        var height = Math.Max(100, maximum);
        plot.Axes.SetLimits(dates[0] - 0.5, dates[^1] + 0.5, -0.02 * height, 1.11 * height);
        MarkSelectedDay();
    }

    private static double[] IndexToMean(
        CodexRhythmDailyTrendPoint[] days, DateOnly? partialDate, Func<CodexRhythmDailyTrendPoint, double?> select)
    {
        var mean = days.Where(day => day.Date != partialDate).Average(select) ?? days.Average(select) ?? 0;
        return days.Select(select).Select(value => value switch
        {
            { } amount when mean > 0 => 100 * (amount / mean),
            0 => 0,
            _ => double.NaN
        }).ToArray();
    }

    private void DailyTrend_DateClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is not WpfPlot control) return;
        var mouse = e.GetPosition(control);
        var scale = control.DisplayScale;
        var coordinate = control.Plot.GetCoordinates((float)(mouse.X * scale), (float)(mouse.Y * scale));
        var limits = control.Plot.Axes.GetLimits();
        if (!double.IsFinite(coordinate.X) || !double.IsFinite(coordinate.Y) ||
            coordinate.X <= limits.Left || coordinate.X >= limits.Right ||
            coordinate.Y <= limits.Bottom || coordinate.Y >= limits.Top) return;

        var date = DateOnly.FromDateTime(DateTime.FromOADate(Math.Round(coordinate.X)));
        if (dailyUsage.FirstOrDefault(day => day.Date == date) is not { } row) return;
        DayGrid.SelectedItem = row;
        DayGrid.ScrollIntoView(row);
        e.Handled = true;
    }

    private void MarkSelectedDay()
    {
        var day = DayGrid.SelectedItem as CodexRhythmDailyUsage;
        var x = day?.Date.ToDateTime(TimeOnly.MinValue).ToOADate();
        var plot = DailyOverlayPlot.Plot;
        if (plot.GetPlottables().OfType<VerticalLine>().FirstOrDefault() is not { } line) return;
        var limits = plot.Axes.GetLimits();
        line.IsVisible = x is { } date && date >= limits.Left && date <= limits.Right;
        if (x.HasValue) line.X = x.Value;
        DailyOverlayPlot.Refresh();
    }
}
