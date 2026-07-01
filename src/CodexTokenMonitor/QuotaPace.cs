namespace CodexTokenMonitor;

internal sealed record QuotaPaceReport(
    decimal UsedPercent,
    decimal ExpectedUsedPercent,
    decimal DeltaPercent,
    decimal RemainingPercent,
    DateTimeOffset ResetNowExpiresLocal,
    string Rating);

internal sealed record WeeklyQuotaPaceEvaluation(
    string ExpectedText,
    string RhythmText);

internal static class QuotaPaceAnalyzer
{
    public static QuotaPaceReport Analyze(CodexQuotaWindowEstimate window)
    {
        var totalMinutes = Math.Max(1d, window.WindowMinutes);
        var elapsedMinutes = Math.Clamp(
            (window.WindowEndLocal - window.WindowStartLocal).TotalMinutes,
            0d,
            totalMinutes);
        var expectedUsed = (decimal)(elapsedMinutes / totalMinutes) * 100m;
        var used = Math.Clamp(window.UsedPercent, 0m, 100m);
        var delta = used - expectedUsed;

        return new QuotaPaceReport(
            used,
            expectedUsed,
            delta,
            Math.Max(0m, 100m - used),
            window.WindowEndLocal.AddMinutes(window.WindowMinutes),
            RatingFor(used, delta));
    }

    public static string FormatShort(CodexQuotaWindowEstimate? window)
    {
        if (window is null)
        {
            return "";
        }

        var report = Analyze(window);
        return $"已{report.UsedPercent:N0}% / 应{report.ExpectedUsedPercent:N0}% · {report.Rating}";
    }

    public static string FormatDetailed(CodexQuotaWindowEstimate? window)
    {
        if (window is null)
        {
            return "";
        }

        var report = Analyze(window);
        return
            $"当前节点 已{report.UsedPercent:N0}% / 应{report.ExpectedUsedPercent:N0}% · " +
            $"{FormatDelta(report.DeltaPercent)} · {report.Rating} · " +
            $"重置后至 {report.ResetNowExpiresLocal:MM-dd HH:mm}";
    }

    public static WeeklyQuotaPaceEvaluation FormatWeeklyCycle(
        CodexQuotaCycle period,
        decimal? usedPercent,
        decimal usedCost,
        decimal? estimatedLimit,
        Func<decimal, string> formatMoney)
    {
        if (usedPercent is null)
        {
            return new WeeklyQuotaPaceEvaluation("-", "-");
        }

        var expectedPercent = ExpectedPercent(period);
        var deltaPercent = usedPercent.Value - expectedPercent;
        var deltaCost = estimatedLimit is { } limit
            ? usedCost - limit * expectedPercent / 100m
            : (decimal?)null;

        return new WeeklyQuotaPaceEvaluation(
            $"{expectedPercent:N0}%",
            FormatWeeklyRhythm(deltaPercent, deltaCost, formatMoney));
    }

    private static decimal ExpectedPercent(CodexQuotaCycle period)
    {
        var elapsedMinutes = Math.Clamp(
            (period.PeriodEnd - period.PeriodStart).TotalMinutes,
            0d,
            7d * 24d * 60d);

        return Math.Clamp((decimal)(elapsedMinutes / (7d * 24d * 60d)) * 100m, 0m, 100m);
    }

    private static string FormatWeeklyRhythm(
        decimal deltaPercent,
        decimal? deltaCost,
        Func<decimal, string> formatMoney)
    {
        if (Math.Abs(deltaPercent) < 1m)
        {
            return "均衡";
        }

        var prefix = deltaPercent > 0m ? "赚" : "亏";
        var percentText = $"{Math.Abs(deltaPercent):N0}%";
        return deltaCost is null
            ? $"{prefix} {percentText}"
            : $"{prefix} {percentText} / {formatMoney(Math.Abs(deltaCost.Value))}";
    }

    private static string FormatDelta(decimal delta)
    {
        if (delta >= 1m)
        {
            return $"超前 {delta:N0}%";
        }

        if (delta <= -1m)
        {
            return $"节省 {Math.Abs(delta):N0}%";
        }

        return "接近均衡";
    }

    private static string RatingFor(decimal used, decimal delta)
    {
        if (used >= 90m || delta >= 25m)
        {
            return "重置价值高";
        }

        if (used >= 75m || delta >= 10m)
        {
            return "可考虑重置";
        }

        if (delta <= -10m)
        {
            return "不急";
        }

        return "按需";
    }
}
