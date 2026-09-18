namespace CodexTokenMonitor;

internal sealed record CodexQuotaCycle(
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    DateTimeOffset ResetAt,
    int SnapshotCount,
    decimal? MaxWeekUsedPercent,
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

internal sealed class CodexQuotaCycleReader
{
    private static readonly TimeSpan CycleCacheLifetime = TimeSpan.FromMinutes(2);
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
    private static readonly TimeSpan TransientCycleMaxDuration = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan TransientResetRunMaxDuration = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan EstablishedResetRunMinDuration = TimeSpan.FromMinutes(10);
    private const int TransientResetRunMaxSnapshots = 5;
    private const decimal TransientResetMaxUsedPercent = 5m;
    private const decimal HistoricalReplayUsedTolerance = 3m;
    private readonly object CycleCacheSync = new();
    private CycleCacheKey? cachedKey;
    private DateTimeOffset cycleCachedAtUtc;
    private IReadOnlyList<CodexQuotaCycle> cachedCycles = Array.Empty<CodexQuotaCycle>();

    public IReadOnlyList<CodexQuotaCycle> ReadWeeklyCycles(
        CodexQuotaEstimate? currentQuota,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var diagnostics = CacheOperationDiagnostics.Begin(propagateToParent: true);
        var cacheKey = CycleCacheKey.From(currentQuota);
        if (cacheKey is not null)
        {
            lock (CycleCacheSync)
            {
                if (cacheKey == cachedKey && DateTimeOffset.UtcNow - cycleCachedAtUtc <= CycleCacheLifetime)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return cachedCycles;
                }
            }
        }

        var snapshots = UsageSourceReaders.Codex.ReadCachedAndHistoricalQuotaSnapshots(
                DefaultStart,
                now.AddMinutes(1),
                cancellationToken)
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
        cancellationToken.ThrowIfCancellationRequested();
        snapshots = RemoveTransientResetOutliers(snapshots, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var periods = BuildActualWeeklyPeriods(snapshots, now, cancellationToken);

        var currentWeek = currentQuota?.Week;
        var currentReset = currentWeek?.ResetAtLocal;
        if (currentQuota is not null && currentWeek is not null && currentReset is not null)
        {
            var currentPeriod = periods.FirstOrDefault(item =>
                IsSameQuotaReset(item.ResetAt, currentReset) &&
                item.PeriodStart <= currentQuota.SnapshotLocal &&
                item.PeriodEnd >= currentQuota.SnapshotLocal.AddSeconds(-1));
            if (currentPeriod is not null)
            {
                var index = periods.IndexOf(currentPeriod);
                periods[index] = currentPeriod with
                {
                    // A restored quota estimate can be older than this refresh. Do not
                    // shrink the live cycle back to the stale snapshot's window end.
                    PeriodEnd = currentPeriod.PeriodEnd >= currentWeek.WindowEndLocal
                        ? currentPeriod.PeriodEnd
                        : currentWeek.WindowEndLocal,
                    ResetAt = currentReset.Value,
                    IsCurrent = true
                };
            }
        }

        var result = periods
            .Where(item => item.PeriodEnd > item.PeriodStart)
            .OrderByDescending(item => item.PeriodStart)
            .ToList();
        // A fallback produced after a storage failure must not turn into an
        // apparently successful cache hit on the next query.
        if (cacheKey is not null && diagnostics.Warnings.Count == 0)
        {
            lock (CycleCacheSync)
            {
                cachedKey = cacheKey;
                cycleCachedAtUtc = DateTimeOffset.UtcNow;
                cachedCycles = result;
            }
        }

        return result;
    }

    public void InvalidateCache()
    {
        lock (CycleCacheSync)
        {
            cachedKey = null;
            cycleCachedAtUtc = default;
            cachedCycles = Array.Empty<CodexQuotaCycle>();
        }
    }

    public static bool IsSameQuotaReset(DateTimeOffset? first, DateTimeOffset? second)
    {
        if (first is null || second is null)
        {
            return true;
        }

        return (first.Value - second.Value).Duration() <= ResetClusterTolerance;
    }

    internal static List<CodexQuotaCycle> BuildActualWeeklyPeriods(
        IReadOnlyList<CodexQuotaSnapshot> snapshots,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var periods = new List<CodexQuotaCycle>();
        if (snapshots.Count == 0)
        {
            return periods;
        }

        var current = new List<CodexQuotaSnapshot> { snapshots[0] };
        for (var index = 1; index < snapshots.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
        IEnumerable<CodexQuotaSnapshot> source,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshots = source
            .OrderBy(item => item.SnapshotLocal)
            .ToList();
        cancellationToken.ThrowIfCancellationRequested();
        if (snapshots.Count < 3)
        {
            return snapshots;
        }

        var current = snapshots;
        var anomalies = new HashSet<CodexQuotaSnapshot>();
        var changed = true;
        while (changed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            changed = false;
            var runs = BuildResetRuns(current);
            if (runs.Count < 3)
            {
                break;
            }

            var removeIndexes = new HashSet<int>();
            for (var index = 1; index < runs.Count - 1; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var previous = runs[index - 1];
                var run = runs[index];
                var next = runs[index + 1];
                if (!IsTransientResetOutlier(previous, run, next))
                {
                    continue;
                }

                for (var snapshotIndex = run.StartIndex; snapshotIndex <= run.EndIndex; snapshotIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    removeIndexes.Add(snapshotIndex);
                    anomalies.Add(current[snapshotIndex]);
                }
            }

            // The account endpoint can temporarily replay the preceding quota
            // window after a newer window has already been established. While
            // the bug is still active there is no following run to form the
            // usual A-B-A sandwich, so recognize a trailing replay only when it
            // matches a reset window that was genuinely observed earlier.
            if (removeIndexes.Count == 0 && IsTrailingHistoricalResetReplay(runs))
            {
                var run = runs[^1];
                for (var snapshotIndex = run.StartIndex; snapshotIndex <= run.EndIndex; snapshotIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
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
            .Select(item =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return anomalies.Contains(item) ? item with { IsAnomaly = true } : item;
            })
            .ToList();
    }

    internal static List<CodexQuotaSnapshot> RemoveTransientResetOutliers(
        IEnumerable<CodexQuotaSnapshot> snapshots,
        CancellationToken cancellationToken = default)
    {
        return MarkTransientResetOutliers(snapshots, cancellationToken)
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

        // A weekly reset cannot move backward and then return to the already
        // established newer reset. This is a stale-window replay even when it
        // lasts tens of minutes or reports a high used percentage.
        if (ResetMovedBackward(previous.ResetAt, run.ResetAt))
        {
            return true;
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

    private static bool IsTrailingHistoricalResetReplay(IReadOnlyList<ResetRun> runs)
    {
        if (runs.Count < 3)
        {
            return false;
        }

        var replay = runs[^1];
        var established = runs[^2];
        if (!ResetMovedBackward(established.ResetAt, replay.ResetAt) ||
            established.Count < 2 ||
            established.Last.SnapshotLocal - established.First.SnapshotLocal < EstablishedResetRunMinDuration ||
            replay.FirstWeekUsedPercent is not { } replayFirstUsed)
        {
            return false;
        }

        var historical = runs
            .Take(runs.Count - 2)
            .LastOrDefault(candidate => IsSameTransientReset(candidate.ResetAt, replay.ResetAt));
        return historical?.LastWeekUsedPercent is { } historicalLastUsed &&
               Math.Abs(replayFirstUsed - historicalLastUsed) <= HistoricalReplayUsedTolerance;
    }

    private static bool ResetMovedBackward(DateTimeOffset? established, DateTimeOffset? candidate)
    {
        return established is not null &&
               candidate is not null &&
               candidate.Value < established.Value.Subtract(ResetClusterTolerance);
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

        // Reset jitter can contain many snapshots and stale high usage. Drop the
        // completed short period before it can shift the next cycle's start.
        // A newly started current cycle must remain visible.
        if (!isCurrent && periodEnd - periodStart <= TransientCycleMaxDuration)
        {
            return;
        }

        if (periodEnd <= periodStart)
        {
            periodEnd = periodStart.AddSeconds(1);
        }

        var usedPercents = snapshots
            .Select(item => item.WeekUsedPercent)
            .Where(item => item is not null)
            .Select(item => item!.Value)
            .ToList();
        periods.Add(new CodexQuotaCycle(
            periodStart,
            periodEnd,
            nominalReset != default ? nominalReset : periodEnd,
            snapshots.Count,
            usedPercents.Count > 0 ? usedPercents.Max() : null,
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
            FirstWeekUsedPercent = First.WeekUsedPercent;
            LastWeekUsedPercent = Last.WeekUsedPercent;
        }

        public int StartIndex { get; }

        public int EndIndex { get; }

        public IReadOnlyList<CodexQuotaSnapshot> Snapshots { get; }

        public CodexQuotaSnapshot First { get; }

        public CodexQuotaSnapshot Last { get; }

        public DateTimeOffset? ResetAt { get; }

        public int Count => EndIndex - StartIndex + 1;

        public decimal MaxWeekUsedPercent { get; }

        public decimal? FirstWeekUsedPercent { get; }

        public decimal? LastWeekUsedPercent { get; }
    }

    private sealed record CycleCacheKey(
        string CachePath,
        string? LimitId,
        DateTimeOffset WeekStartLocal,
        DateTimeOffset WeekEndLocal,
        DateTimeOffset? WeekResetAtLocal)
    {
        public static CycleCacheKey? From(CodexQuotaEstimate? quota)
        {
            return quota?.Week is not { } week || week.ResetAtLocal is null
                ? null
                : new CycleCacheKey(
                    UsageCacheStore.GetCachePath("CodexTokenMonitor"),
                    quota.LimitId,
                    week.WindowStartLocal,
                    week.WindowEndLocal,
                    week.ResetAtLocal);
        }
    }
}
