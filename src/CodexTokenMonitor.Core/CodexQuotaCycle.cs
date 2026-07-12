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
    private static readonly TimeSpan TransientResetMatchTolerance = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan WeeklyQuotaWindow = TimeSpan.FromDays(7);
    private static readonly TimeSpan TransientResetRunMaxDuration = TimeSpan.FromMinutes(3);
    private const int TransientResetRunMaxSnapshots = 5;
    private const decimal TransientResetMaxUsedPercent = 5m;

    public static IReadOnlyList<CodexQuotaCycle> ReadWeeklyCycles(
        CodexQuotaEstimate? currentQuota,
        DateTimeOffset now)
    {
        var snapshots = CodexUsageReader.ReadCachedAndHistoricalQuotaSnapshots(DefaultStart, now.AddMinutes(1))
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
        snapshots = RemoveTransientResetOutliers(snapshots);
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
                AddWeeklyPeriod(periods, current, snapshot.SnapshotLocal, isCurrent: false, nextCycleFirstSnapshot: snapshot);
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

    internal static IReadOnlyList<CodexQuotaSnapshot> MarkTransientResetOutliers(
        IEnumerable<CodexQuotaSnapshot> source)
    {
        var snapshots = source
            .OrderBy(item => item.SnapshotLocal)
            .ToList();
        if (snapshots.Count < 3)
        {
            return snapshots;
        }

        var current = snapshots;
        var anomalies = new HashSet<CodexQuotaSnapshot>();
        var changed = true;
        while (changed)
        {
            changed = false;
            var runs = BuildResetRuns(current);
            if (runs.Count < 3)
            {
                break;
            }

            var removeIndexes = new HashSet<int>();
            for (var index = 1; index < runs.Count - 1; index++)
            {
                var previous = runs[index - 1];
                var run = runs[index];
                var next = runs[index + 1];
                if (!IsTransientResetOutlier(previous, run, next))
                {
                    continue;
                }

                for (var snapshotIndex = run.StartIndex; snapshotIndex <= run.EndIndex; snapshotIndex++)
                {
                    removeIndexes.Add(snapshotIndex);
                    anomalies.Add(current[snapshotIndex]);
                }
            }

            if (removeIndexes.Count == 0)
            {
                break;
            }

            current = current
                .Where((_, index) => !removeIndexes.Contains(index))
                .ToList();
            changed = true;
        }

        return snapshots
            .Select(item => anomalies.Contains(item) ? item with { IsAnomaly = true } : item)
            .ToList();
    }

    internal static List<CodexQuotaSnapshot> RemoveTransientResetOutliers(
        IEnumerable<CodexQuotaSnapshot> snapshots)
    {
        return MarkTransientResetOutliers(snapshots)
            .Where(item => !item.IsAnomaly)
            .ToList();
    }

    private static List<ResetRun> BuildResetRuns(IReadOnlyList<CodexQuotaSnapshot> snapshots)
    {
        var runs = new List<ResetRun>();
        if (snapshots.Count == 0)
        {
            return runs;
        }

        var start = 0;
        for (var index = 1; index < snapshots.Count; index++)
        {
            if (IsSameTransientReset(
                    snapshots[index - 1].WeekResetAtLocal,
                    snapshots[index].WeekResetAtLocal))
            {
                continue;
            }

            runs.Add(new ResetRun(start, index - 1, snapshots));
            start = index;
        }

        runs.Add(new ResetRun(start, snapshots.Count - 1, snapshots));
        return runs;
    }

    private static bool IsTransientResetOutlier(ResetRun previous, ResetRun run, ResetRun next)
    {
        if (!IsSameTransientReset(previous.ResetAt, next.ResetAt) ||
            IsSameTransientReset(previous.ResetAt, run.ResetAt))
        {
            return false;
        }

        var duration = run.Last.SnapshotLocal - run.First.SnapshotLocal;
        var isTinyRun =
            run.Count <= TransientResetRunMaxSnapshots ||
            duration.Duration() <= TransientResetRunMaxDuration;
        if (!isTinyRun)
        {
            return false;
        }

        return run.MaxWeekUsedPercent <= TransientResetMaxUsedPercent;
    }

    private static bool IsSameTransientReset(DateTimeOffset? first, DateTimeOffset? second)
    {
        if (first is null || second is null)
        {
            return first is null && second is null;
        }

        return (first.Value - second.Value).Duration() <= TransientResetMatchTolerance;
    }

    private static void AddWeeklyPeriod(
        List<CodexQuotaCycle> periods,
        IReadOnlyList<CodexQuotaSnapshot> snapshots,
        DateTimeOffset periodEnd,
        bool isCurrent,
        CodexQuotaSnapshot? nextCycleFirstSnapshot = null)
    {
        if (snapshots.Count == 0)
        {
            return;
        }

        var observedStart = snapshots[0].SnapshotLocal;
        var periodStart = observedStart;
        var nominalReset = snapshots
            .Select(item => item.WeekResetAtLocal)
            .Where(item => item is not null)
            .Select(item => item!.Value)
            .OrderBy(item => item)
            .LastOrDefault();
        if (!isCurrent &&
            nominalReset != default &&
            nominalReset > observedStart &&
            nominalReset < periodEnd)
        {
            periodEnd = nominalReset;
        }

        if (!isCurrent &&
            nextCycleFirstSnapshot?.WeekResetAtLocal is { } nextReset)
        {
            var nextAnchoredStart = nextReset - WeeklyQuotaWindow;
            if (nextAnchoredStart > observedStart && nextAnchoredStart < periodEnd)
            {
                periodEnd = nextAnchoredStart;
            }
        }

        if (nominalReset != default)
        {
            var anchoredStart = nominalReset - WeeklyQuotaWindow;
            if (anchoredStart < periodEnd)
            {
                periodStart = anchoredStart;
            }
        }

        if (periods.Count > 0 && periodStart < periods[^1].PeriodEnd)
        {
            periodStart = periods[^1].PeriodEnd;
        }

        if (periodEnd <= periodStart)
        {
            periodEnd = periodStart.AddSeconds(1);
        }

        periods.Add(new CodexQuotaCycle(
            periodStart,
            periodEnd,
            nominalReset != default ? nominalReset : periodEnd,
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
               resetMovedForward ||
               resetChanged && usedDropped;
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

    private sealed class ResetRun
    {
        public ResetRun(int startIndex, int endIndex, IReadOnlyList<CodexQuotaSnapshot> source)
        {
            StartIndex = startIndex;
            EndIndex = endIndex;
            Snapshots = source.Skip(startIndex).Take(endIndex - startIndex + 1).ToList();
            First = Snapshots[0];
            Last = Snapshots[^1];
            ResetAt = First.WeekResetAtLocal;
            MaxWeekUsedPercent = Snapshots
                .Select(item => item.WeekUsedPercent)
                .Where(item => item is not null)
                .Select(item => item!.Value)
                .DefaultIfEmpty(100m)
                .Max();
        }

        public int StartIndex { get; }

        public int EndIndex { get; }

        public IReadOnlyList<CodexQuotaSnapshot> Snapshots { get; }

        public CodexQuotaSnapshot First { get; }

        public CodexQuotaSnapshot Last { get; }

        public DateTimeOffset? ResetAt { get; }

        public int Count => EndIndex - StartIndex + 1;

        public decimal MaxWeekUsedPercent { get; }
    }
}
