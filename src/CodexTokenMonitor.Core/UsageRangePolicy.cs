namespace CodexTokenMonitor;

internal enum AutomaticRefreshAction
{
    RefreshLive,
    AdvanceCurrentPeriod,
    RefreshQuotaOnly
}

internal static class UsageRangePolicy
{
    private static readonly TimeSpan LiveEndTolerance = TimeSpan.FromSeconds(2);

    public static bool ShouldReadLiveToday(SelectedRange range, DateTimeOffset nowLocal)
    {
        ArgumentNullException.ThrowIfNull(range);

        var localNow = nowLocal.ToOffset(CodexUsageReader.BeijingOffset);
        return range.Start <= localNow &&
               range.End >= localNow.Subtract(LiveEndTolerance);
    }

    public static AutomaticRefreshAction GetAutomaticRefreshAction(
        RangeMode mode, SelectedRange range, SelectedRange? lastDisplayedRange, DateTimeOffset nowLocal)
    {
        // A range that followed the clock on the last refresh can become
        // historical at midnight. Advance it before the live-range check.
        if (mode is RangeMode.Day or RangeMode.Week or RangeMode.Month &&
            !range.FollowsCurrent && lastDisplayedRange?.FollowsCurrent == true &&
            lastDisplayedRange.Mode == mode && lastDisplayedRange.Start == range.Start)
        {
            return AutomaticRefreshAction.AdvanceCurrentPeriod;
        }

        return ShouldReadLiveToday(range, nowLocal)
            ? AutomaticRefreshAction.RefreshLive
            : AutomaticRefreshAction.RefreshQuotaOnly;
    }

    /// <summary>
    /// Maps the selected module state (plus the currently shown Codex cycle)
    /// onto the concrete query range. Moved verbatim from the main window so
    /// range resolution is testable without WPF controls.
    /// </summary>
    public static SelectedRange ResolveSelectedRange(
        RangeMode mode,
        DateTimeOffset pickerValue,
        DateTimeOffset? customStartLocal,
        CodexQuotaCycle? cycle,
        DateTimeOffset now)
    {
        var selected = pickerValue;
        var selectedDay = new DateTimeOffset(selected.Year, selected.Month, selected.Day, 0, 0, 0, CodexUsageReader.BeijingOffset);
        var selectedDateTime = new DateTimeOffset(selected.Year, selected.Month, selected.Day, selected.Hour, selected.Minute, selected.Second, CodexUsageReader.BeijingOffset);

        if (customStartLocal is not null)
        {
            var startFromNow = customStartLocal.Value;
            var customEnd = now < startFromNow ? startFromNow : now;
            return new SelectedRange(startFromNow, customEnd, $"当前起算 {startFromNow:MM-dd HH:mm:ss}", "事件明细（起点后）", RangeMode.Day, true);
        }

        if (mode == RangeMode.Cycle)
        {
            if (cycle is null)
            {
                return new SelectedRange(now, now, "额度周期", "按天明细（额度周期）", RangeMode.Cycle);
            }

            var cycleEnd = cycle.IsCurrent ? now : cycle.PeriodEnd;
            if (cycleEnd < cycle.PeriodStart)
            {
                cycleEnd = cycle.PeriodStart;
            }

            return new SelectedRange(
                cycle.PeriodStart,
                cycleEnd,
                cycle.IsCurrent ? "当前周期" : $"周期 {cycle.PeriodStart:MM-dd HH:mm}",
                "按天明细（额度周期）",
                RangeMode.Cycle,
                FollowsCurrent: cycle.IsCurrent);
        }

        DateTimeOffset start;
        DateTimeOffset periodEnd;
        string title;
        string breakdownTitle;
        bool followsCurrent;
        switch (mode)
        {
            case RangeMode.Week:
                periodEnd = selectedDateTime > now ? now : selectedDateTime;
                start = periodEnd.AddDays(-7);
                followsCurrent = periodEnd >= now.AddSeconds(-2);
                title = followsCurrent ? "近一周" : $"7天至 {periodEnd:MM-dd HH:mm}";
                breakdownTitle = "按天明细（7天窗口）";
                break;
            case RangeMode.Month:
                start = new DateTimeOffset(selectedDay.Year, selectedDay.Month, 1, 0, 0, 0, CodexUsageReader.BeijingOffset);
                periodEnd = start.AddMonths(1);
                followsCurrent = start.Year == now.Year && start.Month == now.Month;
                title = followsCurrent ? "本月" : start.ToString("yyyy-MM");
                breakdownTitle = "按天明细（本月）";
                break;
            default:
                start = selectedDay;
                periodEnd = start.AddDays(1);
                followsCurrent = start.Date == now.Date;
                title = followsCurrent ? "今天" : start.ToString("yyyy-MM-dd");
                breakdownTitle = "事件明细（当天）";
                break;
        }

        var end = periodEnd > now ? now : periodEnd;
        if (end < start)
        {
            end = start;
        }

        return new SelectedRange(
            start,
            end,
            title,
            breakdownTitle,
            mode,
            FollowsCurrent: followsCurrent);
    }
}
