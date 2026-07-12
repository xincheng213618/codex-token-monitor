namespace CodexTokenMonitor;

internal static class QuotaEstimateCalculator
{
    private const decimal MinimumStableQuotaDeltaPercent = 3m;

    public static IReadOnlyList<QuotaCurrentWindowRow> BuildCurrentRows(CodexQuotaEstimate currentQuota)
    {
        return new[]
        {
            BuildCurrentRow("5h", currentQuota.FiveHour),
            BuildCurrentRow("7d", currentQuota.Week)
        };
    }

    public static IReadOnlyList<CodexQuotaCycle> BuildWeeklyPeriods(CodexQuotaEstimate currentQuota, DateTimeOffset now)
    {
        return CodexQuotaCycleReader.ReadWeeklyCycles(currentQuota, now);
    }

    public static QuotaEstimateLoadResult BuildLoadResult(
        CodexQuotaEstimate currentQuota,
        DateTimeOffset now,
        IReadOnlyList<CodexQuotaCycle>? knownWeeklyPeriods = null,
        CancellationToken cancellationToken = default)
    {
        var currentRows = BuildCurrentRows(currentQuota);
        cancellationToken.ThrowIfCancellationRequested();

        var periods = HasCurrentPeriod(knownWeeklyPeriods, currentQuota)
            ? knownWeeklyPeriods!
            : BuildWeeklyPeriods(currentQuota, now);
        var usageCache = UsageCacheStore.Load();
        var weeklyRows = new List<QuotaWeeklyCycleRow>(periods.Count);
        foreach (var period in periods)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = BuildWeeklyRow(period, currentQuota.Week, usageCache, now);
            if (row is not null)
            {
                weeklyRows.Add(row);
            }
        }

        return new QuotaEstimateLoadResult(currentRows, weeklyRows, periods.Count);
    }

    private static bool HasCurrentPeriod(
        IReadOnlyList<CodexQuotaCycle>? periods,
        CodexQuotaEstimate currentQuota)
    {
        var resetAt = currentQuota.Week?.ResetAtLocal;
        return periods is { Count: > 0 } &&
               resetAt is not null &&
               periods.Any(period =>
                   period.IsCurrent &&
                   period.PeriodStart <= currentQuota.SnapshotLocal &&
                   period.PeriodEnd >= currentQuota.SnapshotLocal &&
                   CodexQuotaCycleReader.IsSameQuotaReset(period.ResetAt, resetAt));
    }

    public static QuotaWeeklyCycleRow? BuildWeeklyRow(CodexQuotaCycle period, CodexQuotaWindowEstimate? currentWeek)
    {
        return BuildWeeklyRow(period, currentWeek, null, DateTimeOffset.UtcNow.ToOffset(CodexUsageReader.BeijingOffset));
    }

    private static QuotaWeeklyCycleRow? BuildWeeklyRow(
        CodexQuotaCycle period,
        CodexQuotaWindowEstimate? currentWeek,
        UsageCacheStore? usageCache,
        DateTimeOffset now)
    {
        if (period.PeriodEnd <= period.PeriodStart)
        {
            return null;
        }

        var usage = period.IsCurrent && currentWeek is not null
            ? currentWeek.Usage
            : (usageCache ?? UsageCacheStore.Load()).ReadRange(period.PeriodStart, period.PeriodEnd);
        if (usage.Events == 0 && period.Snapshots.Count == 0)
        {
            return null;
        }

        var usedPercents = period.Snapshots
            .Select(item => item.WeekUsedPercent)
            .Where(item => item is not null)
            .Select(item => item!.Value)
            .ToList();
        if (period.IsCurrent && currentWeek is not null)
        {
            usedPercents.Add(currentWeek.UsedPercent);
        }

        var maxUsed = usedPercents.Count > 0 ? usedPercents.Max() : (decimal?)null;
        var usedCost = usage.EstimateCost(PriceProfiles.PrimaryCodex);
        var estimatedLimit = maxUsed is > 0m
            ? usedCost / (maxUsed.Value / 100m)
            : (decimal?)null;
        var plan = SubscriptionPlanStore.Summarize(period.PeriodStart, period.PeriodEnd);
        var pace = QuotaPaceAnalyzer.FormatWeeklyCycle(period, maxUsed, usedCost, estimatedLimit, FormatMoney);

        return new QuotaWeeklyCycleRow(
            $"{period.PeriodStart:MM-dd HH:mm} - {period.PeriodEnd:MM-dd HH:mm}",
            FormatCycleDuration(period, now),
            period.ResetAt.ToString("MM-dd HH:mm"),
            period.Snapshots.Count.ToString("N0"),
            FormatRemainingChange(maxUsed),
            maxUsed is null ? "-" : $"{maxUsed.Value:N0}%",
            pace.ExpectedText,
            pace.RhythmText,
            FormatTokenMillions(usage.TotalTokens),
            FormatMoney(usedCost),
            FormatNullableMoney(estimatedLimit),
            plan.PlanNames,
            plan.HasRecords ? FormatCny(plan.AmountCny) : "-");
    }

    private static string FormatCycleDuration(CodexQuotaCycle period, DateTimeOffset now)
    {
        var effectiveEnd = period.IsCurrent && now < period.PeriodEnd ? now : period.PeriodEnd;
        var duration = effectiveEnd - period.PeriodStart;
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        var prefix = period.IsCurrent ? "进行中 " : "";
        if (duration.TotalDays >= 1)
        {
            return duration.Hours > 0
                ? $"{prefix}{(int)duration.TotalDays}天{duration.Hours}小时"
                : $"{prefix}{(int)duration.TotalDays}天";
        }

        if (duration.TotalHours >= 1)
        {
            return duration.Minutes > 0
                ? $"{prefix}{(int)duration.TotalHours}小时{duration.Minutes}分"
                : $"{prefix}{(int)duration.TotalHours}小时";
        }

        return $"{prefix}{Math.Max(1, (int)Math.Ceiling(duration.TotalMinutes))}分";
    }

    public static string BuildManualWeekEstimate(CodexQuotaEstimate currentQuota, decimal firstRemainingInput, decimal secondRemainingInput)
    {
        var week = currentQuota.Week;
        if (week is null)
        {
            return "没有当前 7d 额度窗口";
        }

        var fromRemaining = Math.Max(firstRemainingInput, secondRemainingInput);
        var toRemaining = Math.Min(firstRemainingInput, secondRemainingInput);
        var requestedDelta = fromRemaining - toRemaining;
        if (requestedDelta <= 0)
        {
            return "请选择不同的剩余百分比";
        }

        var startUsedThreshold = 100m - fromRemaining;
        var endUsedThreshold = 100m - toRemaining;
        var resetAt = week.ResetAtLocal ?? week.WindowEndLocal;
        var snapshots = CodexQuotaCycleReader.RemoveTransientResetOutliers(
            CodexUsageReader.ReadCachedAndHistoricalQuotaSnapshots(week.WindowStartLocal, week.WindowEndLocal.AddMinutes(1))
            .Where(CodexUsageReader.IsGeneralCodexQuotaSnapshot)
            .Where(item =>
                item.WeekUsedPercent is not null &&
                CodexQuotaCycleReader.IsSameQuotaReset(item.WeekResetAtLocal, resetAt)))
            .Append(new CodexQuotaSnapshot(
                currentQuota.SnapshotLocal,
                currentQuota.LimitId,
                currentQuota.LimitName,
                null,
                null,
                week.UsedPercent,
                resetAt))
            .GroupBy(item => item.SnapshotLocal)
            .Select(group => group.OrderByDescending(item => item.WeekUsedPercent ?? -1m).First())
            .OrderBy(item => item.SnapshotLocal)
            .ToList();

        if (snapshots.Count < 2)
        {
            return "当前 7d 快照太少，暂时不能按区间估算";
        }

        var start = snapshots.FirstOrDefault(item => item.WeekUsedPercent >= startUsedThreshold);
        if (start is null)
        {
            return $"当前周还没进入剩余 {fromRemaining:N0}% 附近";
        }

        var end = snapshots.FirstOrDefault(item =>
            item.SnapshotLocal > start.SnapshotLocal &&
            item.WeekUsedPercent >= endUsedThreshold);
        if (end is null)
        {
            return $"当前周还没到剩余 {toRemaining:N0}%";
        }

        var startUsed = start.WeekUsedPercent!.Value;
        var endUsed = end.WeekUsedPercent!.Value;
        var observedDelta = endUsed - startUsed;
        if (observedDelta <= 0)
        {
            return "区间内没有可用的额度下降";
        }

        var usage = CodexUsageReader.ReadCachedRange(start.SnapshotLocal, end.SnapshotLocal);
        var usedCost = usage.EstimateCost(PriceProfiles.PrimaryCodex);
        var estimatedLimit = usedCost / (observedDelta / 100m);
        var actualFromRemaining = 100m - startUsed;
        var actualToRemaining = 100m - endUsed;

        return
            $"{actualFromRemaining:N0}%->{actualToRemaining:N0}% ({observedDelta:N0}%) " +
            $"{start.SnapshotLocal:MM-dd HH:mm}-{end.SnapshotLocal:HH:mm}，" +
            $"{FormatTokenMillions(usage.TotalTokens)}，{FormatMoney(usedCost)}，100%≈{FormatMoney(estimatedLimit)}";
    }

    private static QuotaCurrentWindowRow BuildCurrentRow(string label, CodexQuotaWindowEstimate? window)
    {
        if (window is null)
        {
            return new QuotaCurrentWindowRow(label, "-", "-", "", "");
        }

        var snapshots = CodexQuotaCycleReader.RemoveTransientResetOutliers(
            CodexUsageReader.ReadCachedAndHistoricalQuotaSnapshots(window.WindowStartLocal, window.WindowEndLocal.AddMinutes(1))
                .Where(CodexUsageReader.IsGeneralCodexQuotaSnapshot));
        var delta = BuildQuotaDeltaEstimate(
            label,
            snapshots,
            label == "5h" ? item => item.FiveHourUsedPercent : item => item.WeekUsedPercent,
            label == "5h" ? item => item.FiveHourResetAtLocal : item => item.WeekResetAtLocal);
        var plan = SubscriptionPlanStore.Summarize(window.WindowStartLocal, window.WindowEndLocal);
        var remaining = Math.Max(0m, 100m - window.UsedPercent);
        var usedCost = window.Usage.EstimateCost(PriceProfiles.PrimaryCodex);
        var estimatedLimit = window.UsedPercent > 0m
            ? usedCost / (window.UsedPercent / 100m)
            : (decimal?)null;
        var detail =
            $"{FormatTokenMillions(window.Usage.TotalTokens)} · " +
            $"{FormatMoney(usedCost)} · 100% {FormatNullableMoney(estimatedLimit)}";
        var planText = plan.HasRecords
            ? $"{plan.PlanNames} · {FormatCny(plan.AmountCny)}"
            : "";
        var stableText =
            $"{window.WindowStartLocal:MM-dd HH:mm}-{window.WindowEndLocal:MM-dd HH:mm} · {FormatDelta(delta)}";
        return new QuotaCurrentWindowRow(
            label,
            $"{remaining:N0}%",
            detail,
            planText,
            stableText);
    }

    private static QuotaDeltaEstimate? BuildQuotaDeltaEstimate(
        string label,
        IReadOnlyList<CodexQuotaSnapshot> snapshots,
        Func<CodexQuotaSnapshot, decimal?> usedPercentSelector,
        Func<CodexQuotaSnapshot, DateTimeOffset?> resetAtSelector)
    {
        var ordered = snapshots.OrderBy(item => item.SnapshotLocal).ToList();
        if (ordered.Count < 2)
        {
            return null;
        }

        var current = ordered[^1];
        var currentUsed = usedPercentSelector(current);
        if (currentUsed is null)
        {
            return null;
        }

        var currentReset = resetAtSelector(current);
        var candidates = new List<QuotaDeltaCandidate>();
        for (var index = 0; index < ordered.Count - 1; index++)
        {
            var previous = ordered[index];
            var previousUsed = usedPercentSelector(previous);
            if (previousUsed is null)
            {
                continue;
            }

            var usedDelta = currentUsed.Value - previousUsed.Value;
            if (usedDelta <= 0)
            {
                continue;
            }

            if (CodexQuotaCycleReader.IsSameQuotaReset(resetAtSelector(previous), currentReset))
            {
                candidates.Add(new QuotaDeltaCandidate(previous, previousUsed.Value, usedDelta));
            }
        }

        var candidate = candidates
            .Where(item => item.UsedDeltaPercent >= MinimumStableQuotaDeltaPercent)
            .OrderByDescending(item => item.UsedDeltaPercent)
            .ThenBy(item => item.Snapshot.SnapshotLocal)
            .FirstOrDefault();
        candidate ??= candidates
            .OrderByDescending(item => item.UsedDeltaPercent)
            .ThenBy(item => item.Snapshot.SnapshotLocal)
            .FirstOrDefault();
        if (candidate is null)
        {
            return null;
        }

        var usage = CodexUsageReader.ReadCachedRange(candidate.Snapshot.SnapshotLocal, current.SnapshotLocal);
        var usedCost = usage.EstimateCost(PriceProfiles.PrimaryCodex);
        var estimatedLimit = usedCost / (candidate.UsedDeltaPercent / 100m);
        return new QuotaDeltaEstimate(
            label,
            candidate.Snapshot.SnapshotLocal,
            current.SnapshotLocal,
            100m - candidate.PreviousUsedPercent,
            100m - currentUsed.Value,
            candidate.UsedDeltaPercent,
            usage,
            usedCost,
            estimatedLimit,
            candidate.UsedDeltaPercent >= MinimumStableQuotaDeltaPercent);
    }

    private static string FormatDelta(QuotaDeltaEstimate? estimate)
    {
        if (estimate is null)
        {
            return "-";
        }

        var reliability = estimate.IsStable ? "稳定" : "参考";
        return $"{reliability} {estimate.PreviousRemainingPercent:N0}%->{estimate.CurrentRemainingPercent:N0}% ({estimate.UsedDeltaPercent:N0}%), {FormatMoney(estimate.UsedCost)}, 100%≈{FormatMoney(estimate.EstimatedLimit)}";
    }

    private static string FormatRemainingChange(decimal? maxUsed)
    {
        if (maxUsed is null)
        {
            return "-";
        }

        return $"{Math.Max(0m, 100m - maxUsed.Value):N0}%";
    }

    private static string FormatTokenMillions(long value)
    {
        return $"{value / 1_000_000d:N3}M";
    }

    private static string FormatMoney(decimal value)
    {
        var symbol = PriceProfiles.PrimaryCodex.CurrencySymbol;
        var prefix = string.Equals(symbol, "Credits", StringComparison.OrdinalIgnoreCase)
            ? "Credits "
            : symbol;
        return value switch
        {
            >= 100 => $"{prefix}{value:N0}",
            >= 10 => $"{prefix}{value:N2}",
            >= 1 => $"{prefix}{value:N3}",
            _ => $"{prefix}{value:N4}"
        };
    }

    private static string FormatNullableMoney(decimal? value)
    {
        return value is null ? "-" : FormatMoney(value.Value);
    }

    private static string FormatCny(decimal value)
    {
        return value switch
        {
            >= 100 => $"¥{value:N0}",
            >= 10 => $"¥{value:N2}",
            >= 1 => $"¥{value:N2}",
            _ => $"¥{value:N4}"
        };
    }

    private sealed record QuotaDeltaEstimate(
        string Label,
        DateTimeOffset StartLocal,
        DateTimeOffset EndLocal,
        decimal PreviousRemainingPercent,
        decimal CurrentRemainingPercent,
        decimal UsedDeltaPercent,
        TokenUsageSummary Usage,
        decimal UsedCost,
        decimal EstimatedLimit,
        bool IsStable);

    private sealed record QuotaDeltaCandidate(
        CodexQuotaSnapshot Snapshot,
        decimal PreviousUsedPercent,
        decimal UsedDeltaPercent);
}

internal sealed record QuotaCurrentWindowRow(
    string Label,
    string RemainingText,
    string DetailText,
    string PlanText,
    string StableText);

internal sealed record QuotaEstimateLoadResult(
    IReadOnlyList<QuotaCurrentWindowRow> CurrentRows,
    IReadOnlyList<QuotaWeeklyCycleRow> WeeklyRows,
    int PeriodCount);

internal sealed record QuotaWeeklyCycleRow(
    string Period,
    string Duration,
    string ResetAt,
    string SnapshotCount,
    string Remaining,
    string UsedPercent,
    string ExpectedPercent,
    string Rhythm,
    string Tokens,
    string UsedCost,
    string EstimatedLimit,
    string PlanNames,
    string PlanAmount);
