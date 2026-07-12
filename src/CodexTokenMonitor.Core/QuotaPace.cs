namespace CodexTokenMonitor;

internal sealed record QuotaPaceReport(
    decimal UsedPercent,
    decimal ExpectedUsedPercent,
    decimal DeltaPercent,
    decimal RemainingPercent,
    TimeSpan? TimeToNaturalReset,
    DateTimeOffset? EstimatedFullAtLocal,
    decimal ResetWastePercent,
    DateTimeOffset ResetNowExpiresLocal,
    string Rating,
    string DetailText);

internal sealed record WeeklyQuotaPaceEvaluation(
    string ExpectedText,
    string RhythmText);

internal static class QuotaPaceAnalyzer
{
    private const decimal DefaultResetWasteTolerancePercent = 2m;
    private static readonly TimeSpan DefaultGuard = TimeSpan.FromHours(1);
    private static readonly TimeSpan NaturalResetSoon = TimeSpan.FromMinutes(90);

    public static QuotaPaceReport Analyze(
        CodexQuotaWindowEstimate window,
        ResetOpportunitySummary? resetSummary = null)
    {
        var totalMinutes = Math.Max(1d, window.WindowMinutes);
        var elapsedMinutes = Math.Clamp(
            (window.WindowEndLocal - window.WindowStartLocal).TotalMinutes,
            0d,
            totalMinutes);
        var expectedUsed = (decimal)(elapsedMinutes / totalMinutes) * 100m;
        var used = Math.Clamp(window.UsedPercent, 0m, 100m);
        var delta = used - expectedUsed;
        TimeSpan? timeToNaturalReset = window.ResetAtLocal is null
            ? null
            : window.ResetAtLocal.Value - window.WindowEndLocal;
        var remaining = Math.Max(0m, 100m - used);
        var estimatedFullAt = EstimateFullAt(window, used);
        var naturalReset = window.ResetAtLocal ?? window.WindowStartLocal.AddMinutes(window.WindowMinutes);
        var evaluation = EvaluateReset(
            window.WindowEndLocal,
            window.WindowStartLocal,
            naturalReset,
            used,
            remaining,
            estimatedFullAt,
            resetSummary);

        return new QuotaPaceReport(
            used,
            expectedUsed,
            delta,
            remaining,
            timeToNaturalReset,
            estimatedFullAt,
            remaining,
            window.WindowEndLocal.AddMinutes(window.WindowMinutes),
            evaluation.Rating,
            evaluation.DetailText);
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

    private static DateTimeOffset? EstimateFullAt(CodexQuotaWindowEstimate window, decimal usedPercent)
    {
        if (usedPercent <= 0m)
        {
            return null;
        }

        if (usedPercent >= 100m)
        {
            return window.WindowEndLocal;
        }

        var elapsed = window.WindowEndLocal - window.WindowStartLocal;
        if (elapsed <= TimeSpan.Zero)
        {
            return null;
        }

        var estimatedTicks = elapsed.Ticks / ((double)usedPercent / 100d);
        if (double.IsNaN(estimatedTicks) || double.IsInfinity(estimatedTicks) || estimatedTicks > TimeSpan.MaxValue.Ticks)
        {
            return null;
        }

        return window.WindowStartLocal.AddTicks((long)Math.Max(0d, estimatedTicks));
    }

    private static ResetEvaluation EvaluateReset(
        DateTimeOffset now,
        DateTimeOffset windowStart,
        DateTimeOffset naturalReset,
        decimal usedPercent,
        decimal resetWastePercent,
        DateTimeOffset? estimatedFullAt,
        ResetOpportunitySummary? resetSummary)
    {
        var toNaturalReset = naturalReset - now;
        if (toNaturalReset >= TimeSpan.Zero && toNaturalReset <= NaturalResetSoon)
        {
            return new ResetEvaluation("等自然刷新", $"重置损耗{resetWastePercent:N0}% · {FormatDuration(toNaturalReset)}后刷新");
        }

        var availableCards = resetSummary?.AvailableRecords
            .Where(item => item.ExpiresLocal > now)
            .OrderBy(item => item.ExpiresLocal)
            .ToList() ?? new List<ResetOpportunityRecord>();
        if (resetSummary is not null && availableCards.Count == 0)
        {
            return new ResetEvaluation("无重置卡", $"重置损耗{resetWastePercent:N0}%");
        }

        var earliestCard = availableCards.FirstOrDefault();
        var willFillBeforeNatural = estimatedFullAt is not null && estimatedFullAt.Value < naturalReset;
        var nearFull = estimatedFullAt is not null && estimatedFullAt.Value <= now.Add(DefaultGuard);
        var resetWasteAccepted = resetWastePercent <= DefaultResetWasteTolerancePercent;

        if (resetWasteAccepted && willFillBeforeNatural && nearFull)
        {
            return new ResetEvaluation("现在可用", $"重置损耗{resetWastePercent:N0}% · 预计{estimatedFullAt:HH:mm}满");
        }

        if (willFillBeforeNatural)
        {
            var useFrom = estimatedFullAt!.Value - DefaultGuard;
            if (earliestCard is null || useFrom < earliestCard.ExpiresLocal)
            {
                return new ResetEvaluation("等快满", $"重置损耗{resetWastePercent:N0}% · 建议{useFrom:MM-dd HH:mm}后");
            }
        }

        if (earliestCard is not null && earliestCard.ExpiresLocal < naturalReset)
        {
            var projectedUsedAtExpire = ProjectUsedAt(windowStart, now, earliestCard.ExpiresLocal, usedPercent);
            var projectedResetWaste = Math.Max(0m, 100m - projectedUsedAtExpire);
            if (projectedResetWaste <= DefaultResetWasteTolerancePercent)
            {
                return new ResetEvaluation("临期可用", $"过期时预计损耗{projectedResetWaste:N0}%");
            }

            return new ResetEvaluation("不建议", $"过期时预计损耗{projectedResetWaste:N0}%");
        }

        if (toNaturalReset >= TimeSpan.Zero && toNaturalReset <= TimeSpan.FromHours(12))
        {
            return new ResetEvaluation("不急", $"重置损耗{resetWastePercent:N0}% · {FormatDuration(toNaturalReset)}后刷新");
        }

        return new ResetEvaluation("按需", $"重置损耗{resetWastePercent:N0}%");
    }

    private static decimal ProjectUsedAt(
        DateTimeOffset windowStart,
        DateTimeOffset now,
        DateTimeOffset target,
        decimal usedPercent)
    {
        var elapsed = (now - windowStart).TotalMinutes;
        var targetElapsed = (target - windowStart).TotalMinutes;
        if (usedPercent <= 0m || elapsed <= 0d || targetElapsed <= 0d)
        {
            return 0m;
        }

        return Math.Clamp(usedPercent * (decimal)(targetElapsed / elapsed), 0m, 100m);
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return "0m";
        }

        if (duration.TotalDays >= 1)
        {
            return $"{(int)duration.TotalDays}天{duration.Hours}h";
        }

        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours}h{duration.Minutes}m";
        }

        return $"{Math.Max(1, duration.Minutes)}m";
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

        var paceText = FormatPaceDelta(deltaPercent);
        return deltaCost is null
            ? paceText
            : $"{paceText} / {formatMoney(Math.Abs(deltaCost.Value))}";
    }

    public static string FormatPaceDelta(decimal deltaPercent)
    {
        if (Math.Abs(deltaPercent) < 1m)
        {
            return "基本持平";
        }

        var prefix = deltaPercent > 0m ? "赚" : "亏";
        return $"{prefix}{Math.Abs(deltaPercent):N0}%";
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

    private sealed record ResetEvaluation(string Rating, string DetailText);
}
