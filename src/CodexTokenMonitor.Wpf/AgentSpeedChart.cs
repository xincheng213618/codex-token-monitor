using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace CodexTokenMonitor;

internal sealed class AgentSpeedChart : FrameworkElement
{
    private IReadOnlyList<AgentSpeedPoint> points = [];
    internal void SetData(IReadOnlyList<AgentSpeedPoint> history) { points = history; InvalidateVisual(); }

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        if (ActualWidth < 120 || ActualHeight < 56) return;
        var compact = ActualHeight < 140;
        var plot = new Rect(58, compact ? 12 : 22, ActualWidth - 78, ActualHeight - (compact ? 40 : 54));
        var maximum = Math.Max(10, points.Count > 0 ? points.Max(p => p.Measured + p.Estimated) * 1.15 : 10);
        var muted = (Brush)FindResource("MutedBrush");
        var grid = new Pen((Brush)FindResource("BorderBrush"), 1);
        var steps = compact ? 1 : 4;
        for (var index = 0; index <= steps; index++)
        {
            var y = plot.Bottom - plot.Height * index / steps;
            context.DrawLine(grid, new Point(plot.Left, y), new Point(plot.Right, y));
            Label(context, (maximum * index / steps).ToString("N0"), muted, 3, y - 7);
        }
        if (!compact) Label(context, "tok/s", muted, 3, 0);
        if (points.Count < 2) return;
        var measured = new Pen((Brush)FindResource("AccentBrush"), 2);
        var total = new Pen(new SolidColorBrush(Color.FromRgb(190, 117, 42)), 2) { DashStyle = DashStyles.Dash };
        DrawCurve(context, plot, maximum, measured, false);
        if (points.Any(p => p.Estimated > 0)) DrawCurve(context, plot, maximum, total, true);
        for (var index = 0; index <= 4; index++)
        {
            var point = points[(points.Count - 1) * index / 4];
            Label(context, point.Time.ToOffset(CodexUsageReader.BeijingOffset).ToString("HH:mm:ss"), muted,
                plot.Left + plot.Width * index / 4 - 22, plot.Bottom + 8);
        }
    }

    private void DrawCurve(DrawingContext context, Rect plot, double maximum, Pen pen, bool includeEstimate)
    {
        var geometry = new StreamGeometry();
        using (var drawing = geometry.Open())
        {
            var started = false;
            for (var index = 0; index < points.Count; index++)
            {
                if (includeEstimate && points[index].Estimated <= 0) { started = false; continue; }
                var value = points[index].Measured + (includeEstimate ? points[index].Estimated : 0);
                var point = new Point(plot.Left + plot.Width * index / (points.Count - 1), plot.Bottom - plot.Height * value / maximum);
                if (!started) { drawing.BeginFigure(point, false, false); started = true; }
                else drawing.LineTo(point, true, false);
            }
        }
        geometry.Freeze();
        context.DrawGeometry(null, pen, geometry);
    }

    private void Label(DrawingContext context, string text, Brush brush, double x, double y) => context.DrawText(
        new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"),
            10, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip), new Point(x, y));
}
