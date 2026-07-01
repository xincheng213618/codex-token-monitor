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
        MinHeight = 110;

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
        lastRows = sourceRows
            .Where(row => row.Events > 0)
            .OrderBy(row => row.StartLocal)
            .ToList();

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
        }).ToArray();
        var cachedBars = xValues.Select((x, i) => new Bar
        {
            Position = x,
            Value = cachedValues[i],
            Size = barWidthDays,
        }).ToArray();

        var totalPlot = plot.Add.Bars(totalBars);
        totalPlot.Color = PlotColor.FromSDColor(DrawingColor.FromArgb(92, 119, 183, 171));
        var cachedPlot = plot.Add.Bars(cachedBars);
        cachedPlot.Color = PlotColor.FromSDColor(DrawingColor.FromArgb(210, 32, 148, 128));

        var cumulativePlot = plot.Add.ScatterLine(
            xValues,
            cumulativeValues,
            PlotColor.FromSDColor(DrawingColor.FromArgb(241, 115, 55)));
        cumulativePlot.Axes.YAxis = plot.Axes.Right;
        cumulativePlot.LineWidth = 2.2f;
        cumulativePlot.MarkerSize = 0;

        var bottom = plot.Axes.DateTimeTicksBottom();
        bottom.TickLabelStyle.FontName = "Segoe UI";
        bottom.TickLabelStyle.FontSize = 11;
        bottom.TickLabelStyle.ForeColor = PlotColor.FromSDColor(DrawingColor.FromArgb(86, 100, 118));

        var leftMax = Math.Max(0.001, barValues.Max() * 1.12);
        var rightMax = Math.Max(0.001, cumulativeValues.Last() * 1.08);
        plot.Axes.Margins(0, 0);
        plot.Axes.SetLimitsX(ToDateNumber(startLocal), ToDateNumber(endLocal));
        plot.Axes.SetLimitsY(0, leftMax, plot.Axes.Left);
        plot.Axes.SetLimitsY(0, rightMax, plot.Axes.Right);

        plot.Axes.Left.TickLabelStyle.FontName = "Segoe UI";
        plot.Axes.Left.TickLabelStyle.FontSize = 10;
        plot.Axes.Left.TickLabelStyle.ForeColor = PlotColor.FromSDColor(DrawingColor.FromArgb(116, 128, 145));
        plot.Axes.Right.TickLabelStyle.FontName = "Segoe UI";
        plot.Axes.Right.TickLabelStyle.FontSize = 10;
        plot.Axes.Right.TickLabelStyle.ForeColor = PlotColor.FromSDColor(DrawingColor.FromArgb(116, 128, 145));

        wpfPlot.Refresh();
    }

    private void ConfigureEmptyPlot()
    {
        var plot = wpfPlot.Plot;
        plot.Clear();
        ApplyPlotStyle(plot);
        plot.Axes.SetLimits(0, 1, 0, 1);
        wpfPlot.Refresh();
    }

    private static void ApplyPlotStyle(Plot plot)
    {
        plot.FigureBackground.Color = PlotColor.FromSDColor(DrawingColor.White);
        plot.DataBackground.Color = PlotColor.FromSDColor(DrawingColor.FromArgb(248, 251, 254));
        plot.Grid.MajorLineColor = PlotColor.FromSDColor(DrawingColor.FromArgb(222, 229, 238));
        plot.Grid.MinorLineColor = PlotColor.FromSDColor(DrawingColor.FromArgb(238, 243, 248));
        plot.Grid.MajorLineWidth = 1;
        plot.Grid.MinorLineWidth = 1;
        plot.Axes.Left.FrameLineStyle.Color = PlotColor.FromSDColor(DrawingColor.FromArgb(178, 191, 208));
        plot.Axes.Bottom.FrameLineStyle.Color = PlotColor.FromSDColor(DrawingColor.FromArgb(178, 191, 208));
        plot.Axes.Right.FrameLineStyle.Color = PlotColor.FromSDColor(DrawingColor.FromArgb(214, 223, 235));
        plot.Axes.Top.FrameLineStyle.Color = PlotColor.FromSDColor(DrawingColor.FromArgb(238, 243, 248));
        plot.Axes.Right.IsVisible = true;
        plot.Axes.Top.IsVisible = false;
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
                TotalTokens = values[index].TotalTokens + row.TotalTokens,
                CachedInputTokens = values[index].CachedInputTokens + row.CachedInputTokens
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
