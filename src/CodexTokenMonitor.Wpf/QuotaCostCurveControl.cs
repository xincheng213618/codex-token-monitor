using ScottPlot;
using ScottPlot.WPF;
using DrawingColor = System.Drawing.Color;
using PlotColor = ScottPlot.Color;
using WpfBrushes = System.Windows.Media.Brushes;

namespace CodexTokenMonitor;

internal sealed class QuotaCostCurveControl : System.Windows.Controls.UserControl
{
    private static readonly PlotColor[] HistoricalColors =
    [
        Rgb(55, 126, 184),
        Rgb(255, 127, 14),
        Rgb(148, 103, 189),
        Rgb(214, 39, 40),
        Rgb(23, 190, 207),
        Rgb(227, 119, 194),
        Rgb(188, 145, 0),
        Rgb(76, 114, 176),
        Rgb(196, 78, 82),
        Rgb(129, 114, 178),
        Rgb(204, 121, 167),
        Rgb(100, 181, 205)
    ];

    private readonly WpfPlot wpfPlot = new();
    private static readonly PlotColor CurrentColor = PlotColor.FromSDColor(DrawingColor.FromArgb(0, 112, 82));
    private static readonly PlotColor SelectedColor = PlotColor.FromSDColor(DrawingColor.FromArgb(217, 119, 6));
    private static readonly PlotColor SelectedHaloColor = PlotColor.FromSDColor(DrawingColor.White);

    public QuotaCostCurveControl()
    {
        Background = WpfBrushes.White;
        wpfPlot.Margin = new System.Windows.Thickness(0);
        wpfPlot.Background = WpfBrushes.White;
        Content = wpfPlot;
    }

    public void SetData(IReadOnlyList<QuotaCostCurveSeries> curves, DateTimeOffset? selectedPeriodStart = null)
    {
        var plot = wpfPlot.Plot;
        plot.Clear();
        ApplyStyle(plot);

        var historical = curves.Where(item => !item.IsCurrent).ToList();
        for (var index = 0; index < historical.Count; index++)
        {
            var curve = historical[index];
            if (curve.PeriodStart == selectedPeriodStart)
            {
                continue;
            }

            AddCurve(plot, curve, HistoricalColors[index % HistoricalColors.Length], 1.45f, null);
        }

        var current = curves.FirstOrDefault(item => item.IsCurrent);
        if (current is not null && current.PeriodStart != selectedPeriodStart)
        {
            AddCurve(plot, current, CurrentColor, 4.2f, current.Label);
        }

        var selected = selectedPeriodStart is null
            ? null
            : curves.FirstOrDefault(item => item.PeriodStart == selectedPeriodStart);
        if (selected is not null)
        {
            AddCurve(plot, selected, SelectedHaloColor, 8.5f, null);
            AddCurve(plot, selected, SelectedColor, 5.2f, $"已选 {selected.PeriodStart:MM-dd HH:mm}");
        }

        if (curves.Count > 0)
        {
            plot.Axes.SetLimitsX(0, 100);
            plot.Axes.Margins(0, 0.08);
            plot.ShowLegend(Alignment.UpperLeft);
        }
        else
        {
            plot.Axes.SetLimits(0, 100, 0, 1);
        }

        wpfPlot.Refresh();
    }

    private static void AddCurve(
        Plot plot,
        QuotaCostCurveSeries curve,
        PlotColor color,
        float lineWidth,
        string? legendText)
    {
        var points = BuildSmoothPoints(curve.Points);
        if (points.Xs.Length < 2)
        {
            return;
        }

        var line = plot.Add.ScatterLine(
            points.Xs,
            points.Ys,
            color);
        line.LineWidth = lineWidth;
        line.MarkerSize = 0;
        if (!string.IsNullOrWhiteSpace(legendText))
        {
            line.LegendText = legendText;
        }
    }

    private static (double[] Xs, double[] Ys) BuildSmoothPoints(IReadOnlyList<QuotaCostCurvePoint> source)
    {
        var rawKnots = source
            .GroupBy(item => Math.Round(item.UsedPercent, 3))
            .Select(group => new
            {
                X = group.Key,
                Y = (double)group.Min(item => item.CumulativeCost)
            })
            .OrderBy(item => item.X)
            .ToList();
        var knots = new List<(double X, double Y)>(rawKnots.Count);
        var previousY = 0d;
        foreach (var knot in rawKnots)
        {
            previousY = Math.Max(previousY, knot.Y);
            knots.Add((knot.X, previousY));
        }
        if (knots.Count < 3)
        {
            return (knots.Select(item => item.X).ToArray(), knots.Select(item => item.Y).ToArray());
        }

        var h = new double[knots.Count - 1];
        var slopes = new double[knots.Count - 1];
        for (var index = 0; index < h.Length; index++)
        {
            h[index] = knots[index + 1].X - knots[index].X;
            slopes[index] = h[index] <= 0 ? 0 : (knots[index + 1].Y - knots[index].Y) / h[index];
        }

        var tangents = new double[knots.Count];
        tangents[0] = Math.Max(0, slopes[0]);
        tangents[^1] = Math.Max(0, slopes[^1]);
        for (var index = 1; index < knots.Count - 1; index++)
        {
            if (slopes[index - 1] <= 0 || slopes[index] <= 0)
            {
                tangents[index] = 0;
                continue;
            }

            var previousWeight = 2 * h[index] + h[index - 1];
            var nextWeight = h[index] + 2 * h[index - 1];
            tangents[index] = (previousWeight + nextWeight) /
                              (previousWeight / slopes[index - 1] + nextWeight / slopes[index]);
        }

        var xs = new List<double>();
        var ys = new List<double>();
        for (var index = 0; index < knots.Count - 1; index++)
        {
            var samples = Math.Clamp((int)Math.Ceiling(h[index] * 6), 4, 30);
            for (var sample = 0; sample < samples; sample++)
            {
                var t = sample / (double)samples;
                var t2 = t * t;
                var t3 = t2 * t;
                var h00 = 2 * t3 - 3 * t2 + 1;
                var h10 = t3 - 2 * t2 + t;
                var h01 = -2 * t3 + 3 * t2;
                var h11 = t3 - t2;
                xs.Add(knots[index].X + h[index] * t);
                ys.Add(Math.Max(0,
                    h00 * knots[index].Y +
                    h10 * h[index] * tangents[index] +
                    h01 * knots[index + 1].Y +
                    h11 * h[index] * tangents[index + 1]));
            }
        }

        xs.Add(knots[^1].X);
        ys.Add(knots[^1].Y);
        return (xs.ToArray(), ys.ToArray());
    }

    private static PlotColor Rgb(byte red, byte green, byte blue)
    {
        return PlotColor.FromSDColor(DrawingColor.FromArgb(185, red, green, blue));
    }

    private static void ApplyStyle(Plot plot)
    {
        plot.FigureBackground.Color = PlotColor.FromSDColor(DrawingColor.White);
        plot.DataBackground.Color = PlotColor.FromSDColor(DrawingColor.FromArgb(248, 251, 254));
        plot.Grid.MajorLineColor = PlotColor.FromSDColor(DrawingColor.FromArgb(222, 229, 238));
        plot.Grid.MinorLineColor = PlotColor.FromSDColor(DrawingColor.FromArgb(238, 243, 248));
        var symbol = PriceProfiles.PrimaryCodex.CurrencySymbol;
        plot.Axes.Left.Label.Text = string.Equals(symbol, "Credits", StringComparison.OrdinalIgnoreCase)
            ? "累计等价费用 (Credits)"
            : $"累计等价费用 ({symbol})";
        plot.Axes.Bottom.Label.Text = "7d 已用额度 (%)";
        plot.Axes.Left.Label.FontName = "Microsoft YaHei UI";
        plot.Axes.Bottom.Label.FontName = "Microsoft YaHei UI";
        plot.Axes.Left.TickLabelStyle.FontName = "Segoe UI";
        plot.Axes.Bottom.TickLabelStyle.FontName = "Segoe UI";
        plot.Axes.Left.TickLabelStyle.ForeColor = PlotColor.FromSDColor(DrawingColor.FromArgb(86, 100, 118));
        plot.Axes.Bottom.TickLabelStyle.ForeColor = PlotColor.FromSDColor(DrawingColor.FromArgb(86, 100, 118));
        plot.Axes.Top.IsVisible = false;
        plot.Axes.Right.IsVisible = false;
    }
}
