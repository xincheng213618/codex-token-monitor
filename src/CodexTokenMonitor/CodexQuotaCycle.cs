namespace CodexTokenMonitor;

internal sealed record CodexQuotaCycle(
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    DateTimeOffset ResetAt,
    IReadOnlyList<CodexQuotaSnapshot> Snapshots,
    bool IsCurrent)
{
    public string DisplayText
    {
        get
        {
            var label = IsCurrent ? "当前周期" : "已过期";
            return $"{PeriodStart:MM-dd HH:mm} - {PeriodEnd:MM-dd HH:mm}  {label}";
        }
    }

    public override string ToString()
    {
        return DisplayText;
    }
}

internal static class CodexQuotaCycleReader
{
    private static readonly DateTimeOffset DefaultStart = new(
        2026,
        1,
        1,
        0,
        0,
        0,
        CodexUsageReader.BeijingOffset);

    private static readonly TimeSpan ResetClusterTolerance = TimeSpan.FromMinutes(10);

    public static IReadOnlyList<CodexQuotaCycle> ReadWeeklyCycles(
        CodexQuotaEstimate? currentQuota,
        DateTimeOffset now)
    {
        var snapshots = CodexUsageReader.ReadCachedQuotaSnapshots(DefaultStart, now.AddMinutes(1))
            .Where(item =>
                CodexUsageReader.IsGeneralCodexQuotaSnapshot(item) &&
                item.WeekResetAtLocal is not null &&
                item.WeekUsedPercent is not null)
            .Concat(CurrentWeekSnapshot(currentQuota))
            .Where(CodexUsageReader.IsGeneralCodexQuotaSnapshot)
            .GroupBy(item => item.SnapshotLocal)
            .Select(group => group.OrderByDescending(item => item.WeekUsedPercent ?? -1m).First())
            .OrderBy(item => item.SnapshotLocal)
            .ToList();
        var periods = BuildActualWeeklyPeriods(snapshots, now);

        var currentWeek = currentQuota?.Week;
        var currentReset = currentWeek?.ResetAtLocal;
        if (currentQuota is not null && currentWeek is not null && currentReset is not null)
        {
            var currentPeriod = periods.FirstOrDefault(item =>
                item.Snapshots.Any(snapshot => IsSameQuotaReset(snapshot.WeekResetAtLocal, currentReset)) &&
                item.PeriodStart <= currentQuota.SnapshotLocal &&
                item.PeriodEnd >= currentQuota.SnapshotLocal.AddSeconds(-1));
            if (currentPeriod is not null)
            {
                var index = periods.IndexOf(currentPeriod);
                periods[index] = currentPeriod with
                {
                    PeriodEnd = currentWeek.WindowEndLocal,
                    ResetAt = currentReset.Value,
                    IsCurrent = true
                };
            }
        }

        return periods
            .Where(item => item.PeriodEnd > item.PeriodStart)
            .OrderByDescending(item => item.PeriodStart)
            .ToList();
    }

    public static bool IsSameQuotaReset(DateTimeOffset? first, DateTimeOffset? second)
    {
        if (first is null || second is null)
        {
            return true;
        }

        return (first.Value - second.Value).Duration() <= ResetClusterTolerance;
    }

    private static List<CodexQuotaCycle> BuildActualWeeklyPeriods(
        IReadOnlyList<CodexQuotaSnapshot> snapshots,
        DateTimeOffset now)
    {
        var periods = new List<CodexQuotaCycle>();
        if (snapshots.Count == 0)
        {
            return periods;
        }

        var current = new List<CodexQuotaSnapshot> { snapshots[0] };
        for (var index = 1; index < snapshots.Count; index++)
        {
            var previous = snapshots[index - 1];
            var snapshot = snapshots[index];
            if (StartsNewQuotaCycle(previous, snapshot))
            {
                AddWeeklyPeriod(periods, current, snapshot.SnapshotLocal, isCurrent: false);
                current = new List<CodexQuotaSnapshot> { snapshot };
            }
            else
            {
                current.Add(snapshot);
            }
        }

        AddWeeklyPeriod(periods, current, now, isCurrent: true);
        return periods;
    }

    private static void AddWeeklyPeriod(
        List<CodexQuotaCycle> periods,
        IReadOnlyList<CodexQuotaSnapshot> snapshots,
        DateTimeOffset periodEnd,
        bool isCurrent)
    {
        if (snapshots.Count == 0)
        {
            return;
        }

        var periodStart = snapshots[0].SnapshotLocal;
        var nominalReset = snapshots
            .Select(item => item.WeekResetAtLocal)
            .Where(item => item is not null)
            .Select(item => item!.Value)
            .OrderBy(item => item)
            .LastOrDefault();
        if (!isCurrent &&
            nominalReset != default &&
            nominalReset > periodStart &&
            nominalReset < periodEnd)
        {
            periodEnd = nominalReset;
        }

        if (periodEnd <= periodStart)
        {
            periodEnd = periodStart.AddSeconds(1);
        }

        periods.Add(new CodexQuotaCycle(
            periodStart,
            periodEnd,
            isCurrent
                ? snapshots[^1].WeekResetAtLocal ?? periodEnd
                : periodEnd,
            snapshots,
            isCurrent));
    }

    private static bool StartsNewQuotaCycle(CodexQuotaSnapshot previous, CodexQuotaSnapshot current)
    {
        if (previous.WeekUsedPercent is not { } previousUsed ||
            current.WeekUsedPercent is not { } currentUsed)
        {
            return false;
        }

        var resetChanged = !IsSameQuotaReset(previous.WeekResetAtLocal, current.WeekResetAtLocal);
        var resetMovedForward =
            previous.WeekResetAtLocal is not null &&
            current.WeekResetAtLocal is not null &&
            current.WeekResetAtLocal.Value > previous.WeekResetAtLocal.Value.Add(ResetClusterTolerance);
        var usedDroppedHard = currentUsed <= 2m && previousUsed >= 10m;
        var usedDropped = currentUsed + 2m < previousUsed;

        return usedDroppedHard ||
               resetMovedForward && (usedDropped || currentUsed <= 5m) ||
               resetChanged && usedDroppedHard;
    }

    private static IEnumerable<CodexQuotaSnapshot> CurrentWeekSnapshot(CodexQuotaEstimate? quota)
    {
        if (quota?.Week is null)
        {
            yield break;
        }

        yield return new CodexQuotaSnapshot(
            quota.SnapshotLocal,
            quota.LimitId,
            quota.LimitName,
            null,
            null,
            quota.Week.UsedPercent,
            quota.Week.ResetAtLocal);
    }
}
