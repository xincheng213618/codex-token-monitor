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

internal sealed class QuotaConsumptionTimelineChart : FrameworkElement
{
    private const double PlotLeft = 66;
    private const double PlotRight = 24;
    private const double PlotTop = 38;
    private const double PlotBottom = 66;
    private static readonly Brush TextBrush = FrozenBrush(23, 43, 58);
    private static readonly Brush MutedBrush = FrozenBrush(100, 118, 135);
    private static readonly Brush AccentBrush = FrozenBrush(20, 125, 112);
    private static readonly Brush ForecastBrush = FrozenBrush(190, 117, 42);
    private static readonly Pen GridPen = FrozenPen(FrozenBrush(237, 241, 245), 1);
    private static readonly Pen AxisPen = FrozenPen(FrozenBrush(224, 232, 239), 1);
    private static readonly Pen CurvePen = FrozenPen(AccentBrush, 2.4);
    private static readonly Pen HoverPen = FrozenPen(FrozenBrush(164, 184, 195), 1, dashed: true);
    private static readonly Pen ForecastPen = FrozenPen(ForecastBrush, 2.2, dashed: true);
    private static readonly Pen ResetPen = FrozenPen(MutedBrush, 1.2, dashed: true);
    private readonly ToolTip hoverTip;
    private readonly TextBlock hoverText;
    private QuotaCycleAnalysisResult? result;
    private QuotaCycleTimelinePoint[] samples = [];
    private QuotaTimelineForecastOverlay? forecastOverlay;
    private int? selectedBandIndex;
    private HoverPoint? hoveredPoint;
    private Rect plotArea;
    private DateTimeOffset rangeStart;
    private DateTimeOffset rangeEnd;
    private StreamGeometry? curveGeometry;
    private Rect geometryArea;
    private DateTimeOffset geometryStart;
    private DateTimeOffset geometryEnd;
    private double geometryDpiScale;
    private Point[] renderedPoints = [];

    public QuotaConsumptionTimelineChart()
    {
        SnapsToDevicePixels = true;
        Cursor = Cursors.Cross;
        hoverText = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 430 };
        hoverTip = new ToolTip
        {
            Placement = PlacementMode.Relative,
            PlacementTarget = this,
            StaysOpen = true,
            Background = TextBrush,
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(12, 9, 12, 9),
            Content = hoverText
        };
        MouseMove += OnChartMouseMove;
        MouseLeave += (_, _) => ClearHover();
        MouseLeftButtonDown += OnChartMouseLeftButtonDown;
        IsVisibleChanged += (_, _) => { if (!IsVisible) ClearHover(); };
        Unloaded += (_, _) => ClearHover();
    }

    public event EventHandler<QuotaCycleAnalysisBand>? BandSelected;

    public DateTimeOffset? ViewStart { get; private set; }
    public DateTimeOffset? ViewEnd { get; private set; }
    internal int RenderedPointCount => renderedPoints.Length;

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
        ArgumentNullException.ThrowIfNull(analysis);
        result = analysis;
        // Timeline also contains valid flat observations when there are no cost bands.
        samples = analysis.Timeline.OrderBy(point => point.TimestampLocal).ToArray();
        forecastOverlay = null;
        selectedBandIndex = null;
        ResetVisibleRange();
    }

    public void SetForecast(QuotaTimelineForecastOverlay? overlay)
    {
        if (forecastOverlay == overlay) return;
        forecastOverlay = overlay;
        // An assumption changes only the overlay, never the observed geometry or view state.
        ClearHover();
    }

    public void SetVisibleRange(DateTimeOffset start, DateTimeOffset end)
    {
        if (end <= start) throw new ArgumentOutOfRangeException(nameof(end), "结束时间必须晚于开始时间。");
        ViewStart = start.ToOffset(CodexUsageReader.BeijingOffset);
        ViewEnd = end.ToOffset(CodexUsageReader.BeijingOffset);
        InvalidateCurve();
    }

    public void ResetVisibleRange()
    {
        ViewStart = null;
        ViewEnd = null;
        InvalidateCurve();
    }

    private void InvalidateCurve()
    {
        curveGeometry = null;
        renderedPoints = [];
        ClearHover();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsInfinity(availableSize.Width) ? 860 : Math.Max(0, availableSize.Width),
        double.IsInfinity(availableSize.Height) ? 360 : Math.Max(0, availableSize.Height));

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        drawingContext.DrawRectangle(Brushes.White, null, new Rect(RenderSize));
        plotArea = Rect.Empty;
        if (RenderSize.Width < PlotLeft + PlotRight + 90 || RenderSize.Height < PlotTop + PlotBottom + 50)
            return;

        if (!TryGetRange(out rangeStart, out rangeEnd))
        {
            DrawEmptyState(drawingContext, "暂无额度时间点", "积累额度快照后即可查看真实时间上的消耗变化。");
            return;
        }

        plotArea = new Rect(PlotLeft, PlotTop, RenderSize.Width - PlotLeft - PlotRight,
            RenderSize.Height - PlotTop - PlotBottom);
        DrawAxes(drawingContext);
        var forecast = GetForecastProjection();
        var hasObservedInView = samples.Length > 0 && samples[^1].TimestampLocal >= rangeStart && samples[0].TimestampLocal <= rangeEnd;
        var hasForecastInView = forecast is not null && forecast.Source.StartLocal < rangeEnd && forecast.Source.TargetResetLocal >= rangeStart;
        if (!hasObservedInView && !hasForecastInView)
        {
            DrawEmptyState(drawingContext, "所选范围内没有额度记录", "可切换观察范围，或等待新的额度快照。");
            return;
        }

        if (hasObservedInView) EnsureCurveGeometry();
        drawingContext.PushClip(new RectangleGeometry(plotArea));
        var selected = result?.Bands.FirstOrDefault(band => band.BandIndex == selectedBandIndex);
        Rect? selectionArea = null;
        Brush? selectionBrush = null;
        if (selected is not null && selected.EndLocal >= rangeStart && selected.StartLocal <= rangeEnd)
        {
            var left = Math.Max(plotArea.Left, MapTime(selected.StartLocal));
            var right = Math.Min(plotArea.Right, MapTime(selected.EndLocal));
            selectionArea = new Rect(left, plotArea.Top, Math.Max(2, right - left), plotArea.Height);
            var color = QuotaCycleModelPalette.GetColor(selected.DominantModel);
            selectionBrush = new SolidColorBrush(color);
            var fill = new SolidColorBrush(Color.FromArgb(22, color.R, color.G, color.B));
            drawingContext.DrawRectangle(fill, null, selectionArea.Value);
            var edgePen = new Pen(selectionBrush, 1) { DashStyle = DashStyles.Dash };
            drawingContext.DrawLine(edgePen, selectionArea.Value.TopLeft, selectionArea.Value.BottomLeft);
            drawingContext.DrawLine(edgePen, selectionArea.Value.TopRight, selectionArea.Value.BottomRight);
        }

        if (hasObservedInView && curveGeometry is not null)
        {
            drawingContext.DrawGeometry(null, CurvePen, curveGeometry);
            if (selectionArea is { } area && selectionBrush is not null)
            {
                drawingContext.PushClip(new RectangleGeometry(area));
                drawingContext.DrawGeometry(null, new Pen(selectionBrush, 3.4), curveGeometry);
                drawingContext.Pop();
            }
        }
        if (hasObservedInView && renderedPoints.Length <= 60)
        {
            foreach (var point in renderedPoints)
                drawingContext.DrawEllipse(AccentBrush, new Pen(Brushes.White, 1), point, 3, 3);
        }
        else if (hasObservedInView && renderedPoints.Length > 0)
        {
            drawingContext.DrawEllipse(AccentBrush, new Pen(Brushes.White, 1), renderedPoints[^1], 3.5, 3.5);
        }

        if (hasForecastInView) DrawForecastOverlay(drawingContext, forecast!);

        if (hoveredPoint is { } hover)
        {
            var point = new Point(MapTime(hover.Timestamp), MapUsed(hover.UsedPercent));
            drawingContext.DrawLine(HoverPen, new Point(point.X, plotArea.Top), new Point(point.X, plotArea.Bottom));
            if (!hover.IsForecast || forecast is not null && hover.Timestamp <= forecast.LineEndLocal)
                drawingContext.DrawEllipse(Brushes.White, new Pen(hover.IsForecast ? ForecastBrush : AccentBrush, 2), point, 4.5, 4.5);
        }
        drawingContext.Pop();
        if (hasForecastInView) DrawForecastLabels(drawingContext, forecast!);
        if (samples.Length == 1)
            DrawText(drawingContext, "仅有 1 个观察点", 11, MutedBrush, new Point(plotArea.Right, 12), TextAlignment.Right);
    }

    private bool TryGetRange(out DateTimeOffset start, out DateTimeOffset end)
    {
        start = ViewStart ?? samples.FirstOrDefault()?.TimestampLocal ?? result?.Period.PeriodStart ?? default;
        end = ViewEnd ?? samples.LastOrDefault()?.TimestampLocal ?? result?.Period.PeriodEnd ?? default;
        start = start.ToOffset(CodexUsageReader.BeijingOffset);
        end = end.ToOffset(CodexUsageReader.BeijingOffset);
        if (end > start) return true;
        if (samples.Length == 0) return false;
        // Expand only the coordinate range for a single observation; never add quota anchors.
        start = samples[0].TimestampLocal.ToOffset(CodexUsageReader.BeijingOffset).AddMinutes(-15);
        end = start.AddMinutes(30);
        return true;
    }

    private ForecastProjection? GetForecastProjection()
    {
        if (forecastOverlay is not { } overlay || samples.Length < 2 ||
            samples[^1].TimestampLocal <= samples[0].TimestampLocal ||
            overlay.StartLocal < samples[^1].TimestampLocal || overlay.TargetResetLocal <= overlay.StartLocal ||
            overlay.RemainingPercent is < 0m or > 100m || overlay.RatePerHour < 0m)
            return null;

        var horizonTicks = (overlay.TargetResetLocal - overlay.StartLocal).Ticks;
        DateTimeOffset? exhaustion = null;
        if (overlay.RemainingPercent == 0m)
        {
            exhaustion = overlay.StartLocal;
        }
        else if (overlay.RatePerHour > 0m)
        {
            var horizonHours = (decimal)horizonTicks / TimeSpan.TicksPerHour;
            // Compare before dividing by a potentially tiny rate, so a long-lived forecast cannot overflow a timestamp.
            if (overlay.RatePerHour >= overlay.RemainingPercent / horizonHours)
            {
                var depletionTicks = (long)Math.Clamp(decimal.Ceiling(
                    overlay.RemainingPercent / overlay.RatePerHour * TimeSpan.TicksPerHour), 0m, horizonTicks);
                exhaustion = overlay.StartLocal.AddTicks(depletionTicks);
            }
        }

        return new ForecastProjection(overlay, exhaustion ?? overlay.TargetResetLocal, exhaustion);
    }

    private void DrawForecastOverlay(DrawingContext drawingContext, ForecastProjection forecast)
    {
        var start = forecast.Source.StartLocal > rangeStart ? forecast.Source.StartLocal : rangeStart;
        var end = forecast.LineEndLocal < rangeEnd ? forecast.LineEndLocal : rangeEnd;
        if (end > start)
        {
            drawingContext.DrawLine(ForecastPen,
                new Point(MapTime(start), MapUsed(100m - forecast.RemainingAt(start))),
                new Point(MapTime(end), MapUsed(100m - forecast.RemainingAt(end))));
        }
        if (forecast.ExhaustionLocal is { } exhausted && exhausted >= rangeStart && exhausted <= rangeEnd)
        {
            drawingContext.DrawEllipse(Brushes.White, new Pen(ForecastBrush, 1.8),
                new Point(MapTime(exhausted), plotArea.Bottom), 3.5, 3.5);
        }
        if (forecast.Source.TargetResetLocal >= rangeStart && forecast.Source.TargetResetLocal <= rangeEnd)
        {
            var resetX = MapTime(forecast.Source.TargetResetLocal);
            drawingContext.DrawLine(ResetPen, new Point(resetX, plotArea.Top), new Point(resetX, plotArea.Bottom));
        }
    }

    private void DrawForecastLabels(DrawingContext drawingContext, ForecastProjection forecast)
    {
        var forecastLeft = plotArea.Left + 82;
        var forecastTop = 10d;
        var forecastWidth = plotArea.Right - forecastLeft;
        var resetInsidePlot = false;
        if (forecast.Source.TargetResetLocal >= rangeStart && forecast.Source.TargetResetLocal <= rangeEnd)
        {
            var resetText = $"目标 {forecast.Source.TargetResetLocal.ToOffset(CodexUsageReader.BeijingOffset):MM-dd HH:mm}";
            var resetWidth = Math.Min(148, plotArea.Width);
            var resetLeft = Math.Min(Math.Max(MapTime(forecast.Source.TargetResetLocal) - resetWidth, plotArea.Left),
                Math.Max(plotArea.Left, plotArea.Right - resetWidth));
            resetInsidePlot = resetLeft < forecastLeft;
            var resetTop = resetInsidePlot ? plotArea.Top + 8 : 10;
            DrawBoundedLabel(drawingContext, resetText, MutedBrush, new Point(resetLeft, resetTop), resetWidth);
            if (!resetInsidePlot) forecastWidth = resetLeft - forecastLeft - 12;
        }
        if (forecastWidth < 120)
        {
            forecastLeft = plotArea.Left + 8;
            forecastTop = plotArea.Top + (resetInsidePlot ? 32 : 8);
            forecastWidth = plotArea.Width - 16;
        }

        var label = string.IsNullOrWhiteSpace(forecast.Source.Label) ? "固定速率" : forecast.Source.Label.Trim().Replace('\r', ' ').Replace('\n', ' ');
        if (label.Length > 22) label = label[..20] + "…";
        var outcome = forecast.ExhaustionLocal is { } exhausted
            ? $"预计 {exhausted.ToOffset(CodexUsageReader.BeijingOffset):MM-dd HH:mm} 耗尽"
            : $"目标时预计剩余 {forecast.RemainingAt(forecast.Source.TargetResetLocal):N1}%";
        DrawBoundedLabel(drawingContext, $"预测 · {label} · {outcome}", ForecastBrush,
            new Point(forecastLeft, forecastTop), forecastWidth);
    }

    private void DrawBoundedLabel(DrawingContext drawingContext, string text, Brush brush, Point position, double width)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Microsoft YaHei UI"), 11, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxTextWidth = Math.Max(1, width - 8),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis
        };
        if (position.Y >= plotArea.Top)
            drawingContext.DrawRoundedRectangle(Brushes.White, null,
                new Rect(position.X - 4, position.Y - 2, formatted.Width + 8, formatted.Height + 4), 3, 3);
        drawingContext.DrawText(formatted, position);
    }

    private void DrawAxes(DrawingContext drawingContext)
    {
        DrawText(drawingContext, "剩余额度", 12, TextBrush, new Point(plotArea.Left, 10));
        for (var remaining = 100; remaining >= 0; remaining -= 20)
        {
            var y = MapUsed(100m - remaining);
            drawingContext.DrawLine(GridPen, new Point(plotArea.Left, y), new Point(plotArea.Right, y));
            DrawText(drawingContext, $"{remaining}%", 11, remaining is 100 or 0 ? TextBrush : MutedBrush,
                new Point(plotArea.Left - 12, y - 8), TextAlignment.Right);
        }
        foreach (var tick in BuildTimeTicks())
        {
            var x = MapTime(tick);
            drawingContext.DrawLine(GridPen, new Point(x, plotArea.Top), new Point(x, plotArea.Bottom));
            var alignment = tick == rangeStart ? TextAlignment.Left : tick == rangeEnd ? TextAlignment.Right : TextAlignment.Center;
            var timeFormat = (rangeEnd - rangeStart).TotalMinutes < 5 ? "MM-dd\nHH:mm:ss" : "MM-dd\nHH:mm";
            DrawText(drawingContext, tick.ToString(timeFormat, CultureInfo.InvariantCulture), 11, MutedBrush,
                new Point(x, plotArea.Bottom + 10), alignment);
        }
        drawingContext.DrawLine(AxisPen, plotArea.BottomLeft, plotArea.BottomRight);
        drawingContext.DrawLine(AxisPen, plotArea.BottomLeft, plotArea.TopLeft);
        DrawText(drawingContext, "北京时间 (GMT+8)", 11, MutedBrush,
            new Point(plotArea.Right, RenderSize.Height - 17), TextAlignment.Right);
    }

    private IEnumerable<DateTimeOffset> BuildTimeTicks()
    {
        yield return rangeStart;
        var span = rangeEnd - rangeStart;
        var targetCount = Math.Max(1, (int)(plotArea.Width / 140));
        var desiredSeconds = span.TotalSeconds / targetCount;
        double[] intervals = [60, 120, 300, 600, 900, 1800, 3600, 7200, 10800, 21600, 43200,
            86400, 172800, 604800, 1209600, 2592000, 7776000, 31536000];
        var intervalSeconds = intervals.FirstOrDefault(value => value >= desiredSeconds);
        if (intervalSeconds == 0) intervalSeconds = Math.Ceiling(desiredSeconds / 86400) * 86400;
        var intervalTicks = TimeSpan.FromSeconds(intervalSeconds).Ticks;
        var firstTicks = ((rangeStart.Ticks / intervalTicks) + 1) * intervalTicks;
        var previousX = plotArea.Left;
        for (var ticks = firstTicks; ticks < rangeEnd.Ticks; ticks += intervalTicks)
        {
            var tick = new DateTimeOffset(ticks, CodexUsageReader.BeijingOffset);
            var x = MapTime(tick);
            if (x - previousX < 90 || plotArea.Right - x < 90) continue;
            yield return tick;
            previousX = x;
        }
        yield return rangeEnd;
    }

    private void EnsureCurveGeometry()
    {
        var dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (curveGeometry is not null && geometryArea == plotArea && geometryStart == rangeStart &&
            geometryEnd == rangeEnd && geometryDpiScale == dpiScale) return;

        var first = Math.Max(0, LowerBound(rangeStart) - 1);
        var last = Math.Min(samples.Length - 1, LowerBound(rangeEnd));
        var columns = Math.Clamp((int)Math.Ceiling(plotArea.Width * dpiScale), 1, 8192);
        var points = new List<Point>(Math.Min(last - first + 1, columns * 4 + 2));
        var previousIndex = -1;
        var groupFirst = first;
        while (groupFirst <= last)
        {
            var groupLast = groupFirst;
            var min = groupFirst;
            var max = groupFirst;
            var column = Column(samples[groupFirst]);
            while (groupLast < last && Column(samples[groupLast + 1]) == column)
            {
                groupLast++;
                if (samples[groupLast].UsedPercent < samples[min].UsedPercent) min = groupLast;
                if (samples[groupLast].UsedPercent > samples[max].UsedPercent) max = groupLast;
            }
            Append(groupFirst);
            Append(Math.Min(min, max));
            Append(Math.Max(min, max));
            Append(groupLast);
            groupFirst = groupLast + 1;
        }

        renderedPoints = points.ToArray();
        curveGeometry = new StreamGeometry();
        using (var context = curveGeometry.Open())
        {
            if (points.Count > 0)
            {
                context.BeginFigure(points[0], isFilled: false, isClosed: false);
                for (var i = 1; i < points.Count; i++) context.LineTo(points[i], isStroked: true, isSmoothJoin: true);
            }
        }
        curveGeometry.Freeze();
        geometryArea = plotArea;
        geometryStart = rangeStart;
        geometryEnd = rangeEnd;
        geometryDpiScale = dpiScale;
        return;

        int Column(QuotaCycleTimelinePoint sample) => Math.Clamp((int)Math.Floor(
            (sample.TimestampLocal - rangeStart).Ticks / (double)(rangeEnd - rangeStart).Ticks * columns), -1, columns);

        void Append(int index)
        {
            if (index <= previousIndex) return;
            points.Add(new Point(MapTime(samples[index].TimestampLocal), MapUsed(samples[index].UsedPercent)));
            previousIndex = index;
        }
    }

    private int LowerBound(DateTimeOffset timestamp)
    {
        var left = 0;
        var right = samples.Length;
        while (left < right)
        {
            var middle = left + (right - left) / 2;
            if (samples[middle].TimestampLocal < timestamp) left = middle + 1;
            else right = middle;
        }
        return left;
    }

    private HoverPoint? GetHoverPoint(Point position)
    {
        if (plotArea.IsEmpty || !plotArea.Contains(position) || samples.Length == 0) return null;
        var ratio = Math.Clamp((position.X - plotArea.Left) / plotArea.Width, 0, 1);
        var timestamp = rangeStart.AddTicks((long)((rangeEnd - rangeStart).Ticks * ratio));
        if (timestamp > samples[^1].TimestampLocal)
        {
            var forecast = GetForecastProjection();
            return forecast is not null && timestamp >= forecast.Source.StartLocal && timestamp <= forecast.Source.TargetResetLocal
                ? new HoverPoint(timestamp, 100m - forecast.RemainingAt(timestamp), false, IsForecast: true)
                : null;
        }
        var index = LowerBound(timestamp);
        var nearest = index == samples.Length ? index - 1 : index;
        if (index > 0 && Math.Abs(MapTime(samples[index - 1].TimestampLocal) - position.X) <
            Math.Abs(MapTime(samples[nearest].TimestampLocal) - position.X)) nearest = index - 1;
        if (samples[nearest].TimestampLocal >= rangeStart && samples[nearest].TimestampLocal <= rangeEnd &&
            Math.Abs(MapTime(samples[nearest].TimestampLocal) - position.X) <= 4)
        {
            var point = samples[nearest];
            return new HoverPoint(point.TimestampLocal.ToOffset(CodexUsageReader.BeijingOffset), point.UsedPercent, false);
        }
        if (index == 0 || index == samples.Length) return null;
        var before = samples[index - 1];
        var after = samples[index];
        var ticks = (after.TimestampLocal - before.TimestampLocal).Ticks;
        if (ticks <= 0) return null;
        var fraction = (decimal)(timestamp - before.TimestampLocal).Ticks / ticks;
        return new HoverPoint(timestamp, before.UsedPercent + (after.UsedPercent - before.UsedPercent) * fraction, true);
    }

    private QuotaCycleAnalysisBand? FindBand(HoverPoint point)
    {
        if (point.IsForecast) return null;
        var candidates = result?.Bands.Where(band => point.Timestamp >= band.StartLocal && point.Timestamp <= band.EndLocal).ToList();
        return candidates?.FirstOrDefault(band => point.UsedPercent >= band.UsedFromPercent && point.UsedPercent < band.UsedToPercent)
            ?? candidates?.FirstOrDefault();
    }

    private void OnChartMouseMove(object sender, MouseEventArgs e)
    {
        var position = e.GetPosition(this);
        hoveredPoint = GetHoverPoint(position);
        if (hoveredPoint is not { } point)
        {
            ClearHover();
            return;
        }
        hoverText.Text = BuildHoverText(point);
        hoverTip.HorizontalOffset = position.X + 14;
        hoverTip.VerticalOffset = position.Y + 12;
        hoverTip.IsOpen = true;
        InvalidateVisual();
    }

    private string BuildHoverText(HoverPoint point)
    {
        if (point.IsForecast && GetForecastProjection() is { } forecast)
        {
            var label = string.IsNullOrWhiteSpace(forecast.Source.Label) ? "固定速率" : forecast.Source.Label;
            var outcome = forecast.ExhaustionLocal is { } exhausted
                ? $"预计耗尽 {exhausted.ToOffset(CodexUsageReader.BeijingOffset):MM-dd HH:mm}"
                : $"目标时预计剩余 {forecast.RemainingAt(forecast.Source.TargetResetLocal):N2}%";
            return $"{point.Timestamp:yyyy-MM-dd HH:mm:ss} · 北京时间\n" +
                   $"预测剩余 {Math.Clamp(100m - point.UsedPercent, 0m, 100m):N2}%\n\n" +
                   $"假设：{label} · 保持 {forecast.Source.RatePerHour:N2} 个百分点/小时\n" +
                   $"从 {forecast.Source.StartLocal.ToOffset(CodexUsageReader.BeijingOffset):MM-dd HH:mm} 的 {forecast.Source.RemainingPercent:N2}% 开始\n" +
                   $"{outcome}\n目标时间 {forecast.Source.TargetResetLocal.ToOffset(CodexUsageReader.BeijingOffset):MM-dd HH:mm}";
        }

        var band = FindBand(point);
        var text = $"{point.Timestamp:yyyy-MM-dd HH:mm:ss} · 北京时间\n" +
                   $"剩余 {Math.Clamp(100m - point.UsedPercent, 0m, 100m):N2}%" +
                   (point.IsInterpolated ? "（快照间插值）" : "（对齐点）");
        if (band is not null)
        {
            var duration = band.EndLocal - band.StartLocal;
            var rate = duration > TimeSpan.Zero
                ? $"{band.QuotaDropPercent / (decimal)duration.TotalHours:N2} 个百分点/小时"
                : "暂无速率";
            text += $"\n\n消耗分段 {band.StartLocal.ToOffset(CodexUsageReader.BeijingOffset):MM-dd HH:mm:ss} → {band.EndLocal.ToOffset(CodexUsageReader.BeijingOffset):MM-dd HH:mm:ss}" +
                    $"\n耗时 {FormatDuration(duration)} · 消耗 {band.QuotaDropPercent:N2} 个百分点" +
                    $"\n平均消耗 {rate}\n主导模型 {QuotaCycleModelPalette.ShortName(band.DominantModel)}";
        }
        return text;
    }

    private void OnChartMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (GetHoverPoint(e.GetPosition(this)) is not { } point || FindBand(point) is not { } band) return;
        SelectedBandIndex = band.BandIndex;
        BandSelected?.Invoke(this, band);
    }

    private void ClearHover()
    {
        hoveredPoint = null;
        hoverTip.IsOpen = false;
        InvalidateVisual();
    }

    private double MapTime(DateTimeOffset timestamp) => plotArea.Left + plotArea.Width *
        ((timestamp - rangeStart).Ticks / (double)(rangeEnd - rangeStart).Ticks);

    private double MapUsed(decimal used) => plotArea.Top + plotArea.Height * (double)Math.Clamp(used / 100m, 0m, 1m);

    private void DrawEmptyState(DrawingContext drawingContext, string title, string detail)
    {
        var center = new Point(RenderSize.Width / 2, Math.Max(48, RenderSize.Height / 2 - 22));
        DrawText(drawingContext, title, 15, TextBrush, center, TextAlignment.Center);
        DrawText(drawingContext, detail, 12, MutedBrush, new Point(center.X, center.Y + 30), TextAlignment.Center);
    }

    private void DrawText(DrawingContext drawingContext, string text, double size, Brush brush, Point point,
        TextAlignment alignment = TextAlignment.Left)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Microsoft YaHei UI"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            TextAlignment = alignment
        };
        drawingContext.DrawText(formatted, point);
    }

    private static string FormatDuration(TimeSpan value) => value.TotalDays >= 1
        ? $"{(int)value.TotalDays} 天 {value.Hours} 小时 {value.Minutes} 分钟"
        : value.TotalHours >= 1 ? $"{(int)value.TotalHours} 小时 {value.Minutes} 分钟"
        : value.TotalMinutes >= 1 ? $"{(int)value.TotalMinutes} 分钟 {value.Seconds} 秒"
        : $"{Math.Max(0, value.TotalSeconds):N1} 秒";

    private static Brush FrozenBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Brush brush, double thickness, bool dashed = false)
    {
        var pen = new Pen(brush, thickness);
        if (dashed) pen.DashStyle = DashStyles.Dash;
        pen.Freeze();
        return pen;
    }

    private sealed record HoverPoint(DateTimeOffset Timestamp, decimal UsedPercent, bool IsInterpolated, bool IsForecast = false);

    private sealed record ForecastProjection(
        QuotaTimelineForecastOverlay Source,
        DateTimeOffset LineEndLocal,
        DateTimeOffset? ExhaustionLocal)
    {
        public decimal RemainingAt(DateTimeOffset timestamp)
        {
            if (timestamp <= Source.StartLocal) return Source.RemainingPercent;
            if (ExhaustionLocal is { } exhausted && timestamp >= exhausted) return 0m;
            var bounded = timestamp < Source.TargetResetLocal ? timestamp : Source.TargetResetLocal;
            var hours = (decimal)(bounded - Source.StartLocal).Ticks / TimeSpan.TicksPerHour;
            return Math.Clamp(Source.RemainingPercent - Source.RatePerHour * hours, 0m, 100m);
        }
    }
}

internal sealed record QuotaTimelineForecastOverlay(
    DateTimeOffset StartLocal,
    decimal RemainingPercent,
    decimal RatePerHour,
    DateTimeOffset TargetResetLocal,
    string Label);
