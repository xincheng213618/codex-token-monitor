namespace CodexTokenMonitor;

/// <summary>Refreshes the originally selected cycle; never follows a successor cycle.</summary>
internal sealed record QuotaAnalysisRefreshRange(
    CodexQuotaCycle Period,
    CodexQuotaWindowEstimate? CurrentWeek,
    bool CycleEnded,
    string Reason)
{
    public static QuotaAnalysisRefreshRange ResolveCurrentAnalysisPeriod(
        CodexQuotaCycle original,
        CodexQuotaWindowEstimate? currentWeek,
        DateTimeOffset now,
        IEnumerable<CodexQuotaSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(snapshots);
        if (!original.IsCurrent) return new(original, currentWeek, false, "");

        // IsSameQuotaReset deliberately treats null as compatible elsewhere; a
        // missing reset is not sufficient proof of this window's identity here.
        var matchingWeek = currentWeek?.ResetAtLocal is { } reset &&
                           CodexQuotaCycleReader.IsSameQuotaReset(reset, original.ResetAt) &&
                           currentWeek.WindowStartLocal <= original.PeriodEnd
            ? currentWeek : null;
        var actualStart = matchingWeek?.WindowStartLocal ?? original.PeriodStart;
        var naturalEnd = original.ResetAt < now ? original.ResetAt : now;
        var ordered = snapshots.Where(item => item.SnapshotLocal <= now &&
                CodexUsageReader.IsGeneralCodexQuotaSnapshot(item) && !item.IsAnomaly &&
                item.WeekUsedPercent is >= 0m and <= 100m && item.WeekResetAtLocal is not null)
            .GroupBy(item => item.SnapshotLocal)
            .Select(group => group.OrderByDescending(item => item.WeekUsedPercent).First())
            .OrderBy(item => item.SnapshotLocal).ToArray();
        var trusted = CodexQuotaCycleReader.RemoveTransientResetOutliers(ordered);
        if (trusted.Count == 0)
        {
            return Freeze(original, matchingWeek, actualStart, now,
                "没有可信的通用额度快照，保留原分析范围；刷新额度后重试。");
        }
        var periods = CodexQuotaCycleReader.BuildActualWeeklyPeriods(trusted, now);

        // Match the cycle that overlaps the originally opened range, not just
        // the latest reset. A hard quota drop can split two cycles even when
        // their reported reset values happen to stay inside the 10-minute jitter.
        var hasOriginalObservation = trusted.Any(item => item.SnapshotLocal >= actualStart &&
            item.SnapshotLocal <= original.PeriodEnd &&
            CodexQuotaCycleReader.IsSameQuotaReset(item.WeekResetAtLocal, original.ResetAt));
        var originalCycle = periods
            .Where(item => CodexQuotaCycleReader.IsSameQuotaReset(item.ResetAt, original.ResetAt) &&
                           hasOriginalObservation && item.PeriodStart < original.PeriodEnd && item.PeriodEnd > actualStart)
            .OrderBy(item => (item.PeriodStart - actualStart).Duration())
            .FirstOrDefault();
        DateTimeOffset? observedEnd = originalCycle is { IsCurrent: false }
            ? originalCycle.PeriodEnd : null;

        if (originalCycle is null)
        {
            var successor = periods.Where(item => item.PeriodStart >= original.PeriodEnd &&
                    !CodexQuotaCycleReader.IsSameQuotaReset(item.ResetAt, original.ResetAt))
                .OrderBy(item => item.PeriodStart).FirstOrDefault();
            if (successor is not null)
            {
                observedEnd = successor.PeriodStart;
            }
            else if (trusted.LastOrDefault() is { WeekResetAtLocal: { } latestReset } latest &&
                     latest.SnapshotLocal >= original.PeriodEnd &&
                     !CodexQuotaCycleReader.IsSameQuotaReset(latestReset, original.ResetAt))
            {
                // A different reset is known, but the available snapshots do not
                // establish its boundary. Preserve the last loaded range instead
                // of adding potentially post-reset usage to the old cost band.
                return Ended(original, actualStart, Min(original.PeriodEnd, naturalEnd),
                    "额度重置标识已变化，本页保留已加载范围；请返回额度页选择新周期。");
            }
            else
            {
                return Freeze(original, matchingWeek, actualStart, now,
                    "无法确认原周期或后继边界，保留原分析范围；刷新额度后重试。");
            }
        }

        var end = observedEnd is { } boundary ? Min(naturalEnd, boundary) : naturalEnd;
        if (observedEnd is not null || now >= original.ResetAt)
        {
            return Ended(original, actualStart, end,
                "本页周期已结束，已截断至重置边界；请返回额度页选择新周期。");
        }

        // Do not create a reversed range if the clock moves behind this page's
        // start. Keeping the prior range is safer than manufacturing an anchor.
        if (end <= actualStart)
        {
            return new(original, currentWeek, false, "当前时间早于周期起点，保留原分析范围。");
        }
        return new(original with { PeriodEnd = end },
            matchingWeek is null ? null : matchingWeek with { WindowEndLocal = end }, false, "");
    }

    private static QuotaAnalysisRefreshRange Ended(CodexQuotaCycle original,
        DateTimeOffset actualStart, DateTimeOffset end, string reason) => new(
            original with
            {
                // Build ignores CurrentWeek after IsCurrent becomes false. Carry
                // the previously authoritative start into the historical range.
                PeriodStart = actualStart,
                PeriodEnd = end,
                IsCurrent = false
            }, null, true, reason);

    private static QuotaAnalysisRefreshRange Freeze(CodexQuotaCycle original,
        CodexQuotaWindowEstimate? week, DateTimeOffset actualStart, DateTimeOffset now, string reason)
    {
        var end = Min(original.PeriodEnd, Min(now, original.ResetAt));
        return now >= original.ResetAt
            ? Ended(original, actualStart, end, "本页周期已到期；" + reason)
            : new(original with { PeriodEnd = end },
                week is null ? null : week with { WindowEndLocal = Min(week.WindowEndLocal, end) }, false, reason);
    }

    private static DateTimeOffset Min(DateTimeOffset first, DateTimeOffset second) => first < second ? first : second;
}
