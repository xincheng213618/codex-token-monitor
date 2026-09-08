using ScottPlot;
using ScottPlot.WPF;
using DrawingColor = System.Drawing.Color;
using PlotColor = ScottPlot.Color;
using WpfBrushes = System.Windows.Media.Brushes;

namespace CodexTokenMonitor;

internal sealed class WpfTokenTimelineControl : System.Windows.Controls.UserControl
{
    private readonly WpfPlot wpfPlot = new();
    private DateTimeOffset startLocal;
    private DateTimeOffset endLocal;
    private TimeSpan? fixedBucketInterval;
    private IReadOnlyList<TokenUsageBucket> lastRows = Array.Empty<TokenUsageBucket>();

    public WpfTokenTimelineControl()
    {
        Background = WpfBrushes.White;
        MinHeight = 60;

        wpfPlot.Margin = new System.Windows.Thickness(0);
        wpfPlot.Background = WpfBrushes.White;
        Content = wpfPlot;

        ConfigureEmptyPlot();
    }

    public void SetData(
        DateTimeOffset start,
        DateTimeOffset end,
        IEnumerable<TokenUsageBucket> sourceRows,
        TimeSpan? bucketInterval = null)
    {
        startLocal = start;
        endLocal = end;
        fixedBucketInterval = bucketInterval;
        lastRows = UsageTimelineBuilder.Build(start, end, sourceRows, bucketInterval);

        Render(lastRows);
    }

    public void ClearData()
    {
        fixedBucketInterval = null;
        lastRows = Array.Empty<TokenUsageBucket>();
        ConfigureEmptyPlot();
        wpfPlot.Refresh();
    }

    public void ResetView()
    {
        Render(lastRows);
    }

    private void Render(IReadOnlyList<TokenUsageBucket> rows)
    {
        var plot = wpfPlot.Plot;
        plot.Clear();
        ApplyPlotStyle(plot);

        if (rows.Count == 0 || endLocal <= startLocal)
        {
            plot.Axes.SetLimits(0, 1, 0, 1);
            wpfPlot.Refresh();
            return;
        }

        var bars = BuildBars(rows).Where(bar => bar.TotalTokens > 0).ToList();
        if (bars.Count == 0)
        {
            plot.Axes.SetLimits(0, 1, 0, 1);
            wpfPlot.Refresh();
            return;
        }

        var xValues = bars.Select(bar => ToDateNumber(bar.StartLocal)).ToArray();
        var barValues = bars.Select(bar => bar.TotalTokens / 1_000_000d).ToArray();
        var cachedValues = bars.Select(bar => Math.Min(bar.CachedInputTokens, bar.TotalTokens) / 1_000_000d).ToArray();
        var cumulativeValues = new double[bars.Count];
        double cumulative = 0;
        for (var i = 0; i < bars.Count; i++)
        {
            cumulative += barValues[i];
            cumulativeValues[i] = cumulative;
        }

        var barWidthDays = GetBarWidthDays(bars);
        var totalBars = xValues.Select((x, i) => new Bar
        {
            Position = x,
            Value = barValues[i],
            Size = barWidthDays,
            LineWidth = 0,
        }).ToArray();
        var cachedBars = xValues.Select((x, i) => new Bar
        {
            Position = x,
            Value = cachedValues[i],
            Size = barWidthDays,
            LineWidth = 0,
        }).ToArray();

        var totalPlot = plot.Add.Bars(totalBars);
        totalPlot.Color = PlotColor.FromSDColor(DrawingColor.FromArgb(201, 230, 222));
        totalPlot.LegendText = "总 Token";
        var cachedPlot = plot.Add.Bars(cachedBars);
        cachedPlot.Color = PlotColor.FromSDColor(DrawingColor.FromArgb(20, 125, 112));
        cachedPlot.LegendText = "缓存输入";

        var cumulativePlot = plot.Add.ScatterLine(
            xValues,
            cumulativeValues,
            PlotColor.FromSDColor(DrawingColor.FromArgb(219, 152, 82)));
        cumulativePlot.Axes.YAxis = plot.Axes.Right;
        cumulativePlot.LineWidth = 2.2f;
        cumulativePlot.MarkerSize = 0;
        cumulativePlot.LegendText = "累计总 Token";

        plot.ShowLegend(Alignment.UpperLeft);
        plot.Legend.FontName = "Microsoft YaHei UI";
        plot.Legend.FontSize = 11;

        var bottom = plot.Axes.DateTimeTicksBottom();
        bottom.TickLabelStyle.FontName = "Segoe UI";
        bottom.TickLabelStyle.FontSize = 11;
        bottom.TickLabelStyle.ForeColor = PlotColor.FromSDColor(DrawingColor.FromArgb(100, 118, 135));

        var leftMax = Math.Max(0.001, barValues.Max() * 1.12);
        var rightMax = Math.Max(0.001, cumulativeValues.Last() * 1.08);
        plot.Axes.Margins(0, 0);
        plot.Axes.SetLimitsX(ToDateNumber(startLocal), ToDateNumber(endLocal));
        plot.Axes.SetLimitsY(0, leftMax, plot.Axes.Left);
        plot.Axes.SetLimitsY(0, rightMax, plot.Axes.Right);

        plot.Axes.Left.TickLabelStyle.FontName = "Segoe UI";
        plot.Axes.Left.TickLabelStyle.FontSize = 10;
        plot.Axes.Left.TickLabelStyle.ForeColor = PlotColor.FromSDColor(DrawingColor.FromArgb(100, 118, 135));
        plot.Axes.Right.TickLabelStyle.FontName = "Segoe UI";
        plot.Axes.Right.TickLabelStyle.FontSize = 10;
        plot.Axes.Right.TickLabelStyle.ForeColor = PlotColor.FromSDColor(DrawingColor.FromArgb(100, 118, 135));
        ApplyAxisLabels(plot);

        wpfPlot.Refresh();
    }

    private void ConfigureEmptyPlot()
    {
        var plot = wpfPlot.Plot;
        plot.Clear();
        ApplyPlotStyle(plot);
        plot.Axes.SetLimits(0, 1, 0, 1);
        ApplyAxisLabels(plot);
        wpfPlot.Refresh();
    }

    private void ApplyAxisLabels(Plot plot)
    {
        plot.Axes.Left.Label.Text = fixedBucketInterval switch
        {
            { } interval when interval == TimeSpan.FromMinutes(1) => "每分钟 Token（M）",
            { } interval when interval == TimeSpan.FromMinutes(10) => "每 10 分钟 Token（M）",
            { } interval when interval == TimeSpan.FromHours(1) => "每小时 Token（M）",
            { } interval => $"每 {interval.TotalMinutes:N0} 分钟 Token（M）",
            null => "单次事件 Token（M）"
        };
        plot.Axes.Right.Label.Text = "累计 Token（M）";
        plot.Axes.Left.Label.FontName = "Microsoft YaHei UI";
        plot.Axes.Right.Label.FontName = "Microsoft YaHei UI";
        plot.Axes.Left.Label.FontSize = 12;
        plot.Axes.Right.Label.FontSize = 12;
        plot.Axes.Left.Label.Bold = false;
        plot.Axes.Right.Label.Bold = false;
        plot.Axes.Left.Label.ForeColor = PlotColor.FromSDColor(DrawingColor.FromArgb(100, 118, 135));
        plot.Axes.Right.Label.ForeColor = PlotColor.FromSDColor(DrawingColor.FromArgb(100, 118, 135));
    }

    private static void ApplyPlotStyle(Plot plot)
    {
        plot.FigureBackground.Color = PlotColor.FromSDColor(DrawingColor.White);
        plot.DataBackground.Color = PlotColor.FromSDColor(DrawingColor.White);
        plot.Grid.MajorLineColor = PlotColor.FromSDColor(DrawingColor.FromArgb(237, 241, 245));
        plot.Grid.MinorLineColor = PlotColor.FromSDColor(DrawingColor.Transparent);
        plot.Grid.MajorLineWidth = 1;
        plot.Grid.MinorLineWidth = 0;
        plot.Axes.Left.FrameLineStyle.Color = PlotColor.FromSDColor(DrawingColor.FromArgb(224, 232, 239));
        plot.Axes.Bottom.FrameLineStyle.Color = PlotColor.FromSDColor(DrawingColor.FromArgb(224, 232, 239));
        plot.Axes.Right.FrameLineStyle.Color = PlotColor.FromSDColor(DrawingColor.FromArgb(224, 232, 239));
        plot.Axes.Top.FrameLineStyle.Color = PlotColor.FromSDColor(DrawingColor.FromArgb(237, 241, 245));
        plot.Axes.Right.IsVisible = true;
        plot.Axes.Top.IsVisible = false;
        plot.Legend.IsVisible = false;
        plot.Legend.BackgroundColor = PlotColor.FromSDColor(DrawingColor.White);
        plot.Legend.FontColor = PlotColor.FromSDColor(DrawingColor.FromArgb(23, 43, 58));
        plot.Legend.OutlineWidth = 0;
        plot.Legend.ShadowColor = PlotColor.FromSDColor(DrawingColor.Transparent);
    }

    private IEnumerable<TimelineBar> BuildBars(IReadOnlyList<TokenUsageBucket> rows)
    {
        if (fixedBucketInterval is null)
        {
            return rows
                .Select(row => new TimelineBar(row.StartLocal, row.TotalTokens, row.CachedInputTokens))
                .ToList();
        }

        var interval = fixedBucketInterval.Value;
        var spanTicks = Math.Max(interval.Ticks, (endLocal - startLocal).Ticks);
        var bucketCount = Math.Max(1, (int)Math.Ceiling(spanTicks / (double)interval.Ticks));
        var values = new TimelineBar[bucketCount];
        for (var i = 0; i < bucketCount; i++)
        {
            values[i] = new TimelineBar(startLocal.AddTicks(interval.Ticks * i), 0, 0);
        }

        foreach (var row in rows)
        {
            var offsetTicks = Math.Clamp((row.StartLocal - startLocal).Ticks, 0, spanTicks - 1);
            var index = (int)(offsetTicks / interval.Ticks);
            index = Math.Clamp(index, 0, bucketCount - 1);
            values[index] = values[index] with
            {
                TotalTokens = TokenCountMath.AddNonNegative(values[index].TotalTokens, row.TotalTokens),
                CachedInputTokens = TokenCountMath.AddNonNegative(values[index].CachedInputTokens, row.CachedInputTokens)
            };
        }

        return values;
    }

    private double GetBarWidthDays(IReadOnlyList<TimelineBar> bars)
    {
        if (fixedBucketInterval is not null)
        {
            return fixedBucketInterval.Value.TotalDays * 0.82;
        }

        if (bars.Count <= 1)
        {
            return Math.Max((endLocal - startLocal).TotalDays / 80d, TimeSpan.FromMinutes(1).TotalDays);
        }

        var minGap = bars
            .Zip(bars.Skip(1), (left, right) => (right.StartLocal - left.StartLocal).TotalDays)
            .Where(gap => gap > 0)
            .DefaultIfEmpty(TimeSpan.FromMinutes(1).TotalDays)
            .Min();
        return Math.Max(TimeSpan.FromSeconds(8).TotalDays, minGap * 0.74);
    }

    private static double ToDateNumber(DateTimeOffset value)
    {
        return value.DateTime.ToOADate();
    }

    private sealed record TimelineBar(
        DateTimeOffset StartLocal,
        long TotalTokens,
        long CachedInputTokens);
}
