using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using FlowDirection = System.Windows.FlowDirection;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using ToolTip = System.Windows.Controls.ToolTip;

namespace CodexTokenMonitor;

internal sealed class QuotaCycleAnalysisChart : FrameworkElement
{
    private const double PlotLeft = 76;
    private const double PlotRight = 24;
    private const double PlotTop = 54;
    private const double PlotBottom = 58;
    private readonly List<(Rect Area, QuotaCycleAnalysisBand Band)> hitAreas = [];
    private readonly ToolTip hoverTip;
    private QuotaCycleAnalysisResult? result;
    private int? selectedBandIndex;

    public QuotaCycleAnalysisChart()
    {
        SnapsToDevicePixels = true;
        Cursor = Cursors.Cross;
        hoverTip = new ToolTip
        {
            Placement = PlacementMode.Relative,
            PlacementTarget = this,
            StaysOpen = true,
            Background = new SolidColorBrush(Color.FromRgb(23, 43, 58)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(10, 7, 10, 7)
        };
        MouseMove += OnChartMouseMove;
        MouseLeave += (_, _) => hoverTip.IsOpen = false;
        MouseLeftButtonDown += OnChartMouseLeftButtonDown;
        IsVisibleChanged += (_, _) => { if (!IsVisible) hoverTip.IsOpen = false; };
    }

    public event EventHandler<QuotaCycleAnalysisBand>? BandSelected;

    public int? SelectedBandIndex
    {
        get => selectedBandIndex;
        set
        {
            if (selectedBandIndex == value) return;
            selectedBandIndex = value;
            InvalidateVisual();
        }
    }

    public void SetData(QuotaCycleAnalysisResult analysis)
    {
        result = analysis;
        selectedBandIndex = null;
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 760 : Math.Max(420, availableSize.Width);
        var height = double.IsInfinity(availableSize.Height) ? 360 : Math.Max(250, availableSize.Height);
        return new Size(width, height);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        hitAreas.Clear();
        drawingContext.DrawRectangle(Brushes.White, null, new Rect(RenderSize));
        if (result is not { HasData: true } analysis || RenderSize.Width < PlotLeft + PlotRight + 40 ||
            RenderSize.Height < PlotTop + PlotBottom + 40)
        {
            DrawText(drawingContext, result?.EmptyReason ?? "正在准备分析数据...", 14,
                new SolidColorBrush(Color.FromRgb(100, 118, 135)),
                new Point(24, Math.Max(24, RenderSize.Height / 2 - 10)));
            return;
        }

        var plotRect = new Rect(
            PlotLeft,
            PlotTop,
            RenderSize.Width - PlotLeft - PlotRight,
            RenderSize.Height - PlotTop - PlotBottom);
        var gridPen = new Pen(new SolidColorBrush(Color.FromRgb(237, 241, 245)), 1);
        var axisPen = new Pen(new SolidColorBrush(Color.FromRgb(224, 232, 239)), 1);
        var labelBrush = new SolidColorBrush(Color.FromRgb(100, 118, 135));

        var maximum = Math.Max(1m, analysis.MaximumBandEstimate * 1.12m);
        for (var tick = 0; tick <= 4; tick++)
        {
            var value = maximum * tick / 4m;
            var y = plotRect.Bottom - plotRect.Height * tick / 4d;
            drawingContext.DrawLine(gridPen, new Point(plotRect.Left, y), new Point(plotRect.Right, y));
            DrawText(drawingContext, FormatMoney(value), 11, labelBrush,
                new Point(plotRect.Left - 8, y), TextAlignment.Right);
        }

        for (var remaining = 100; remaining >= 0; remaining -= 10)
        {
            var x = MapUsed(100m - remaining, plotRect);
            drawingContext.DrawLine(gridPen, new Point(x, plotRect.Top), new Point(x, plotRect.Bottom));
            DrawText(drawingContext, $"{remaining}%", 11, labelBrush,
                new Point(x, plotRect.Bottom + 12), TextAlignment.Center);
        }

        drawingContext.DrawLine(axisPen, plotRect.BottomLeft, plotRect.BottomRight);
        drawingContext.DrawLine(axisPen, plotRect.BottomLeft, plotRect.TopLeft);
        DrawText(drawingContext, "剩余额度（100% → 0%）", 12, labelBrush,
            new Point(plotRect.Left + plotRect.Width / 2, RenderSize.Height - 17), TextAlignment.Center);

        var average = analysis.EstimatedFullQuotaCost ?? 0m;
        if (average > 0m)
        {
            var averageY = MapCost(average, maximum, plotRect);
            var averagePen = new Pen(new SolidColorBrush(Color.FromRgb(20, 125, 112)), 1.4)
            {
                DashStyle = DashStyles.Dash
            };
            drawingContext.DrawLine(averagePen, new Point(plotRect.Left, averageY), new Point(plotRect.Right, averageY));
            DrawText(drawingContext, $"全周期均值 {FormatMoney(average)}", 11,
                new SolidColorBrush(Color.FromRgb(20, 125, 112)),
                new Point(plotRect.Right - 4, averageY - 5), TextAlignment.Right);
        }

        DrawModelRail(drawingContext, analysis.Bands, plotRect);

        Point? previous = null;
        foreach (var band in analysis.Bands)
        {
            if (band.EstimatedFullQuotaCost is not { } estimate) continue;
            var x1 = MapUsed(band.UsedFromPercent, plotRect);
            var x2 = MapUsed(band.UsedToPercent, plotRect);
            var center = new Point((x1 + x2) / 2d, MapCost(estimate, maximum, plotRect));
            var color = QuotaCycleModelPalette.GetColor(band.DominantModel);
            var lineBrush = new SolidColorBrush(color);
            var selected = band.BandIndex == selectedBandIndex;

            var fill = new SolidColorBrush(Color.FromArgb(selected ? (byte)42 : (byte)20, color.R, color.G, color.B));
            drawingContext.DrawRoundedRectangle(
                fill,
                selected ? new Pen(lineBrush, 1.6) : null,
                new Rect(Math.Min(x1, x2) + 1, center.Y, Math.Max(2, Math.Abs(x2 - x1) - 2), plotRect.Bottom - center.Y),
                3,
                3);
            if (previous is { } previousPoint)
            {
                drawingContext.DrawLine(new Pen(lineBrush, selected ? 3.2 : 2.2), previousPoint, center);
            }

            drawingContext.DrawEllipse(Brushes.White, new Pen(lineBrush, selected ? 3.4 : 2.4), center,
                selected ? 6.5 : 5, selected ? 6.5 : 5);
            hitAreas.Add((new Rect(Math.Min(x1, x2), plotRect.Top, Math.Max(8, Math.Abs(x2 - x1)), plotRect.Height), band));
            previous = center;
        }

        DrawText(drawingContext, "局部 100% 折算 ($)", 12,
            new SolidColorBrush(Color.FromRgb(23, 43, 58)),
            new Point(8, 18));
    }

    private static void DrawModelRail(
        DrawingContext drawingContext,
        IReadOnlyList<QuotaCycleAnalysisBand> bands,
        Rect plotRect)
    {
        var railRect = new Rect(plotRect.Left, plotRect.Top - 28, plotRect.Width, 14);
        drawingContext.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromRgb(237, 241, 245)),
            null,
            railRect,
            4,
            4);
        foreach (var band in bands)
        {
            var x1 = MapUsed(band.UsedFromPercent, plotRect);
            var x2 = MapUsed(band.UsedToPercent, plotRect);
            var color = QuotaCycleModelPalette.GetColor(band.DominantModel);
            drawingContext.DrawRectangle(
                new SolidColorBrush(color),
                null,
                new Rect(Math.Min(x1, x2), railRect.Top, Math.Max(2, Math.Abs(x2 - x1)), railRect.Height));
        }
    }

    private void OnChartMouseMove(object sender, MouseEventArgs e)
    {
        var position = e.GetPosition(this);
        var hit = hitAreas.FirstOrDefault(item => item.Area.Contains(position));
        if (hit.Band is null)
        {
            hoverTip.IsOpen = false;
            return;
        }

        hoverTip.Content = BuildToolTip(hit.Band);
        hoverTip.HorizontalOffset = position.X + 14;
        hoverTip.VerticalOffset = position.Y + 12;
        hoverTip.IsOpen = true;
    }

    private void OnChartMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var position = e.GetPosition(this);
        var hit = hitAreas.FirstOrDefault(item => item.Area.Contains(position));
        if (hit.Band is null) return;
        SelectedBandIndex = hit.Band.BandIndex;
        BandSelected?.Invoke(this, hit.Band);
    }

    private static string BuildToolTip(QuotaCycleAnalysisBand band)
    {
        var mix = string.Join(" / ", band.Models.Take(3).Select(item =>
            $"{QuotaCycleModelPalette.ShortName(item.ModelId)} {item.QuotaSharePercent:N0}%"));
        return
            $"剩余 {band.RemainingFromPercent:N1}% → {band.RemainingToPercent:N1}%\n" +
            $"{band.StartLocal:MM-dd HH:mm} – {band.EndLocal:MM-dd HH:mm}\n" +
            $"主导 {QuotaCycleModelPalette.ShortName(band.DominantModel)} · {mix}\n" +
            $"掉 {band.QuotaDropPercent:N2}% · {band.Tokens / 1_000_000d:N3}M tokens\n" +
            $"本段 {FormatMoney(band.EquivalentCost)} · 100%≈{FormatMoney(band.EstimatedFullQuotaCost ?? 0m)}";
    }

    private static double MapUsed(decimal usedPercent, Rect plotRect) =>
        plotRect.Left + plotRect.Width * (double)Math.Clamp(usedPercent / 100m, 0m, 1m);

    private static double MapCost(decimal cost, decimal maximum, Rect plotRect) =>
        plotRect.Bottom - plotRect.Height * (double)Math.Clamp(maximum <= 0m ? 0m : cost / maximum, 0m, 1m);

    private static string FormatMoney(decimal value) => value switch
    {
        >= 100 => $"${value:N0}",
        >= 10 => $"${value:N1}",
        >= 1 => $"${value:N2}",
        _ => $"${value:N3}"
    };

    private void DrawText(
        DrawingContext drawingContext,
        string text,
        double size,
        Brush brush,
        Point position,
        TextAlignment alignment = TextAlignment.Left)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Microsoft YaHei UI"),
            size,
            brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            TextAlignment = alignment
        };
        drawingContext.DrawText(formatted, position);
    }
}
