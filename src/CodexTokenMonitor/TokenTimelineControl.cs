using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace CodexTokenMonitor;

internal sealed class TokenTimelineControl : Control
{
    private readonly List<TokenUsageBucket> rows = new();
    private DateTimeOffset startLocal;
    private DateTimeOffset endLocal;
    private TimeSpan? fixedBucketInterval;

    public TokenTimelineControl()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint,
            true);
        BackColor = Color.White;
        MinimumSize = new Size(200, 110);
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
        rows.Clear();
        rows.AddRange(sourceRows.Where(row => row.Events > 0).OrderBy(row => row.StartLocal));
        Invalidate();
    }

    public void ClearData()
    {
        rows.Clear();
        fixedBucketInterval = null;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        graphics.Clear(Color.FromArgb(252, 253, 255));

        var bounds = ClientRectangle;
        if (bounds.Width < 80 || bounds.Height < 60)
        {
            return;
        }

        using var mutedBrush = new SolidBrush(Color.FromArgb(92, 105, 122));
        using var faintBrush = new SolidBrush(Color.FromArgb(246, 249, 252));
        using var gridPen = new Pen(Color.FromArgb(218, 226, 236));
        using var axisPen = new Pen(Color.FromArgb(157, 171, 190));
        using var tickPen = new Pen(Color.FromArgb(180, 192, 208));
        using var lineShadowPen = new Pen(Color.FromArgb(70, 232, 126, 66), 4f);
        using var linePen = new Pen(Color.FromArgb(241, 115, 55), 2.2f);
        using var cachedBrush = new SolidBrush(Color.FromArgb(122, 190, 176));
        using var font = new Font("Microsoft YaHei UI", 8.0f);
        using var valueFont = new Font("Segoe UI", 8.0f, FontStyle.Bold);

        var plot = Rectangle.Inflate(bounds, -14, -8);
        plot.Y += 6;
        plot.Height -= 28;
        if (plot.Height <= 20)
        {
            return;
        }

        if (rows.Count == 0 || endLocal <= startLocal)
        {
            graphics.DrawString("无事件", font, mutedBrush, plot.Left, plot.Top + plot.Height / 2 - 8);
            return;
        }

        var bars = BuildBars().ToList();
        if (bars.Count == 0)
        {
            graphics.DrawString("无事件", font, mutedBrush, plot.Left, plot.Top + plot.Height / 2 - 8);
            return;
        }

        var maxBucket = Math.Max(1, bars.Max(item => item.TotalTokens));
        var total = Math.Max(1, rows.Sum(row => row.TotalTokens));
        graphics.FillRectangle(faintBrush, plot);
        for (var i = 0; i <= 4; i++)
        {
            var y = plot.Bottom - (int)Math.Round(plot.Height * i / 4d);
            graphics.DrawLine(gridPen, plot.Left, y, plot.Right, y);
        }

        DrawTimeTicks(graphics, plot, font, mutedBrush, tickPen);

        var barGap = bars.Count > 48 ? 1 : 2;
        var slot = plot.Width / (float)Math.Max(1, bars.Count);
        var barWidth = fixedBucketInterval is null
            ? Math.Max(1f, Math.Min(4f, slot - barGap))
            : Math.Max(1f, slot - barGap);
        long cumulative = 0;
        var points = new List<PointF>(bars.Count);

        for (var i = 0; i < bars.Count; i++)
        {
            var bar = bars[i];
            var totalValue = bar.TotalTokens;
            cumulative += totalValue;
            var x = fixedBucketInterval is null
                ? XForTime(plot, bar.StartLocal) - barWidth / 2f
                : plot.Left + i * slot;
            var totalHeight = (float)(totalValue / (double)maxBucket * plot.Height);
            var cachedHeight = totalValue > 0
                ? (float)(Math.Min(bar.CachedInputTokens, totalValue) / (double)maxBucket * plot.Height)
                : 0f;
            var totalRect = new RectangleF(x, plot.Bottom - totalHeight, barWidth, totalHeight);
            var cachedRect = new RectangleF(x, plot.Bottom - cachedHeight, barWidth, cachedHeight);
            if (totalRect.Height > 0)
            {
                using var barBrush = new LinearGradientBrush(
                    totalRect,
                    Color.FromArgb(25, 139, 121),
                    Color.FromArgb(112, 190, 174),
                    LinearGradientMode.Vertical);
                graphics.FillRectangle(barBrush, totalRect);
            }

            if (cachedRect.Height > 0)
            {
                graphics.FillRectangle(cachedBrush, cachedRect);
            }

            var cumulativeY = plot.Bottom - (float)(cumulative / (double)total * plot.Height);
            points.Add(new PointF(x + barWidth / 2f, cumulativeY));
        }

        if (points.Count > 1)
        {
            graphics.DrawLines(lineShadowPen, points.ToArray());
            graphics.DrawLines(linePen, points.ToArray());
        }

        graphics.DrawRectangle(axisPen, plot);
        var totalLabel = FormatTokens(rows.Sum(row => row.TotalTokens));
        var totalLabelSize = graphics.MeasureString(totalLabel, valueFont);
        graphics.DrawString(
            totalLabel,
            valueFont,
            mutedBrush,
            plot.Right - totalLabelSize.Width - 4,
            plot.Top + 2);

    }

    private IEnumerable<TimelineBar> BuildBars()
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

    private float XForTime(Rectangle plot, DateTimeOffset value)
    {
        var spanTicks = Math.Max(1, (endLocal - startLocal).Ticks);
        var ratio = Math.Clamp((value - startLocal).Ticks / (double)spanTicks, 0d, 1d);
        return plot.Left + (float)(ratio * plot.Width);
    }

    private void DrawTimeTicks(
        Graphics graphics,
        Rectangle plot,
        Font font,
        Brush brush,
        Pen tickPen)
    {
        var span = endLocal - startLocal;
        if (span <= TimeSpan.Zero)
        {
            return;
        }

        var tickStep = GetTickStep(span, plot.Width);
        var tick = AlignTick(startLocal, tickStep);
        var lastLabelRight = float.MinValue;
        while (tick <= endLocal)
        {
            if (tick >= startLocal)
            {
                var ratio = (tick - startLocal).Ticks / (double)span.Ticks;
                var x = plot.Left + (float)(ratio * plot.Width);
                graphics.DrawLine(tickPen, x, plot.Bottom, x, plot.Bottom + 4);

                var label = FormatTickLabel(tick, span);
                var size = graphics.MeasureString(label, font);
                var labelX = Math.Clamp(x - size.Width / 2f, plot.Left, plot.Right - size.Width);
                if (labelX > lastLabelRight + 8)
                {
                    graphics.DrawString(label, font, brush, labelX, plot.Bottom + 5);
                    lastLabelRight = labelX + size.Width;
                }
            }

            tick = tick.Add(tickStep);
        }
    }

    private static TimeSpan GetTickStep(TimeSpan span, int width)
    {
        if (span.TotalHours <= 25)
        {
            return width >= 900 ? TimeSpan.FromHours(1) : TimeSpan.FromHours(2);
        }

        if (span.TotalDays <= 8)
        {
            return width >= 900 ? TimeSpan.FromHours(12) : TimeSpan.FromDays(1);
        }

        if (span.TotalDays <= 35)
        {
            return width >= 900 ? TimeSpan.FromDays(2) : TimeSpan.FromDays(4);
        }

        return TimeSpan.FromDays(7);
    }

    private static DateTimeOffset AlignTick(DateTimeOffset value, TimeSpan step)
    {
        if (step.TotalHours < 24)
        {
            var hourStep = Math.Max(1, (int)Math.Round(step.TotalHours));
            var alignedHour = value.Hour / hourStep * hourStep;
            var tick = new DateTimeOffset(value.Year, value.Month, value.Day, alignedHour, 0, 0, value.Offset);
            return tick < value ? tick.AddHours(hourStep) : tick;
        }

        var dayStep = Math.Max(1, (int)Math.Round(step.TotalDays));
        var date = new DateTimeOffset(value.Year, value.Month, value.Day, 0, 0, 0, value.Offset);
        var remainder = (date.Day - 1) % dayStep;
        var tickDate = date.AddDays(-remainder);
        return tickDate < value ? tickDate.AddDays(dayStep) : tickDate;
    }

    private static string FormatTickLabel(DateTimeOffset value, TimeSpan span)
    {
        if (span.TotalHours <= 25)
        {
            var hour = value.Hour;
            if (hour == 0)
            {
                return "12 AM";
            }

            if (hour == 12)
            {
                return "12 PM";
            }

            return hour < 12 ? $"{hour} AM" : $"{hour - 12} PM";
        }

        if (span.TotalDays <= 8)
        {
            return value.Hour < 12
                ? $"{value:M/d} AM"
                : $"{value:M/d} PM";
        }

        return value.ToString("M/d");
    }

    private static int GetBucketCount(int width, TimeSpan span)
    {
        var byWidth = Math.Max(12, width / 8);
        var byTime = span.TotalHours switch
        {
            <= 1 => 12,
            <= 3 => 18,
            <= 6 => 24,
            <= 12 => 48,
            _ => 96
        };
        return Math.Clamp(Math.Min(byWidth, byTime), 12, 96);
    }

    private static string FormatTokens(long value)
    {
        if (value >= 1_000_000)
        {
            return $"{value / 1_000_000d:N1}M";
        }

        if (value >= 1_000)
        {
            return $"{value / 1_000d:N1}K";
        }

        return value.ToString("N0");
    }

    private sealed record TimelineBar(
        DateTimeOffset StartLocal,
        long TotalTokens,
        long CachedInputTokens);
}
