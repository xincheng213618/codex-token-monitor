namespace CodexTokenMonitor;

internal sealed class CodexUsageReader
{
    private const string CacheFolder = "CodexTokenMonitor";
    private const string QuotaHistoryFileName = "quota-history-v2.jsonl";
    private const int FiveHourWindowMinutes = 5 * 60;
    private const int WeeklyWindowMinutes = 7 * 24 * 60;
    private const long SparkContextWindowUpperBound = 128_000;
    private const string SparkLimitId = "codex_bengalfox";
    private const string SparkLimitName = "GPT-5.3-Codex-Spark";
    public static readonly TimeSpan BeijingOffset = TimeSpan.FromHours(8);

    private LiveFileTailReader UsageTailReader = new();
    private LiveFileTailReader QuotaTailReader = new();
    private ConcurrentDictionary<string, SubagentReplayFilter> UsageReplayFilters =
        new(StringComparer.OrdinalIgnoreCase);
    private ConcurrentDictionary<string, SubagentReplayFilter> QuotaReplayFilters =
        new(StringComparer.OrdinalIgnoreCase);
    private object QuotaHistoryCacheSync = new();
    private List<RateLimitSnapshot> QuotaHistorySnapshotCache = new();
    private HashSet<QuotaHistoryKey> QuotaHistoryKeyCache = new(QuotaHistoryKeyComparer.Instance);
    private string? quotaHistoryCachedPath;
    private long quotaHistoryCachedLength = -1;
    private DateTime quotaHistoryCachedWriteTimeUtc;

    private readonly record struct RateLimitWindowSnapshot(
        decimal UsedPercent,
        int WindowMinutes,
        DateTimeOffset? ResetAtLocal);

    private readonly record struct RateLimitSnapshot(
        DateTimeOffset TimestampLocal,
        string? LimitId,
        string? LimitName,
        RateLimitWindowSnapshot? FiveHour,
        RateLimitWindowSnapshot? Week,
        long ModelContextWindow);

    private readonly record struct RateLimitScanResult(
        IReadOnlyList<RateLimitSnapshot> Snapshots,
        bool IsComplete);

    private readonly record struct QuotaSnapshotScanResult(
        IReadOnlyList<CodexQuotaSnapshot> Snapshots,
        bool IsComplete);

    private sealed record QuotaHistoryKey(DateTimeOffset SnapshotLocal, string LimitId);

    private sealed class QuotaHistoryKeyComparer : IEqualityComparer<QuotaHistoryKey>
    {
        public static QuotaHistoryKeyComparer Instance { get; } = new();

        public bool Equals(QuotaHistoryKey? first, QuotaHistoryKey? second)
        {
            return ReferenceEquals(first, second) ||
                   first is not null &&
                   second is not null &&
                   first.SnapshotLocal == second.SnapshotLocal &&
                   string.Equals(first.LimitId, second.LimitId, StringComparison.OrdinalIgnoreCase);
        }

        public int GetHashCode(QuotaHistoryKey value)
        {
            return HashCode.Combine(
                value.SnapshotLocal,
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.LimitId));
        }
    }

    private sealed record MaterializedQuotaPoint(
        CodexQuotaSnapshot Snapshot,
        DateTimeOffset? BeforeSnapshotLocal,
        DateTimeOffset? AfterSnapshotLocal);

    public bool ClearCache()
    {
        ResetLiveFileCursors();
        CodexQuotaCycleReader.InvalidateCache();
        var deleted = UsageCacheStore.Delete(CacheFolder);
        lock (QuotaHistoryCacheSync)
        {
            ResetQuotaHistoryCache(GetQuotaHistoryPath());
        }

        return deleted;
    }

    public bool ClearCachedDay(DateOnly date)
    {
        ResetLiveFileCursors();
        CodexQuotaCycleReader.InvalidateCache();
        var usageDeleted = UsageCacheStore.DeleteDay(CacheFolder, date);
        var quotaDeleted = QuotaSnapshotCacheStore.DeleteDay(CacheFolder, date);
        return usageDeleted || quotaDeleted;
    }

    public IReadOnlyList<DateTimeOffset> GetIncompleteHistoricalDays(
        DateTimeOffset startInclusive,
        DateTimeOffset endInclusive,
        CancellationToken cancellationToken = default)
    {
        return UsageCacheStore.GetIncompleteDays(
            CacheFolder,
            startInclusive,
            endInclusive,
            cancellationToken);
    }

    public TokenUsageSummary ReadCachedRange(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        CancellationToken cancellationToken = default)
    {
        return UsageCacheStore.Load(CacheFolder).ReadRange(startLocal, endLocal, cancellationToken);
    }

    public IReadOnlyList<TokenUsageBucket> ReadCachedDetailRows(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        CancellationToken cancellationToken = default)
    {
        return UsageCacheStore.Load(CacheFolder).ReadDetailRows(startLocal, endLocal, cancellationToken);
    }

    public CodexQuotaEstimate? ReadQuotaEstimate(CancellationToken cancellationToken = default)
    {
        return ReadQuotaEstimate(null, cancellationToken);
    }

    public CodexQuotaEstimate? ReadQuotaEstimate(
        CodexQuotaEstimate? establishedQuota,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow.ToOffset(BeijingOffset);
        var directSnapshot = CodexAppServerQuotaReader.ReadCurrent(cancellationToken);
        if (directSnapshot is not null)
        {
            directSnapshot = NormalizeQuotaSnapshotWindows(directSnapshot);
            if (IsGeneralCodexQuotaSnapshot(directSnapshot))
            {
                AppendQuotaHistoryIfNew(ToRateLimitSnapshot(directSnapshot));
                if (HasBackwardWeeklyReset(establishedQuota, directSnapshot))
                {
                    directSnapshot = SelectLatestTrustedQuotaSnapshot(now, directSnapshot, cancellationToken)
                        is { } trusted && QuotaFreshness.IsFresh(trusted.SnapshotLocal, now)
                            ? trusted
                            : directSnapshot;
                }

                return BuildQuotaEstimate(directSnapshot, now, cancellationToken: cancellationToken);
            }
        }

        // Older CLI builds do not expose account/rateLimits/read. Keep the
        // session-log path as a compatibility fallback in that case.
        var liveEnd = now.AddMinutes(5);
        var recentStart = now.AddMinutes(-30);
        var snapshot = ReadQuotaSnapshotsUncached(recentStart, liveEnd, cancellationToken).Snapshots
            .Where(IsGeneralCodexQuotaSnapshot)
            .OrderByDescending(item => item.SnapshotLocal)
            .FirstOrDefault()
            ?? ReadQuotaSnapshotsCached(recentStart, liveEnd, cancellationToken)
            .Where(IsGeneralCodexQuotaSnapshot)
            .OrderByDescending(item => item.SnapshotLocal)
            .FirstOrDefault()
            ?? ReadQuotaSnapshotsCached(StartOfDay(now), liveEnd, cancellationToken)
            .Where(IsGeneralCodexQuotaSnapshot)
            .OrderByDescending(item => item.SnapshotLocal)
            .FirstOrDefault()
            ?? ReadCachedAndHistoricalQuotaSnapshots(now.AddDays(-8), liveEnd, cancellationToken)
            .Where(IsGeneralCodexQuotaSnapshot)
            .OrderByDescending(item => item.SnapshotLocal)
            .FirstOrDefault();
        if (snapshot is null || !QuotaFreshness.IsFresh(snapshot.SnapshotLocal, now))
        {
            return null;
        }

        if (HasBackwardWeeklyReset(establishedQuota, snapshot))
        {
            snapshot = SelectLatestTrustedQuotaSnapshot(now, snapshot, cancellationToken)
                is { } trusted && QuotaFreshness.IsFresh(trusted.SnapshotLocal, now)
                    ? trusted
                    : snapshot;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return BuildQuotaEstimate(snapshot, now, cancellationToken: cancellationToken);
    }

    public CodexQuotaEstimate? ReadCachedQuotaEstimate(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow.ToOffset(BeijingOffset);
        var snapshot = SelectLatestTrustedQuotaSnapshot(now, supplemental: null, cancellationToken);
        if (snapshot is null || !QuotaFreshness.IsFresh(snapshot.SnapshotLocal, now))
        {
            return null;
        }

        return BuildQuotaEstimate(snapshot, now, includeLiveToday: false, cancellationToken);
    }

    private CodexQuotaSnapshot? SelectLatestTrustedQuotaSnapshot(
        DateTimeOffset now,
        CodexQuotaSnapshot? supplemental,
        CancellationToken cancellationToken)
    {
        var snapshots = ReadCachedAndHistoricalQuotaSnapshots(
                now.AddDays(-8),
                now.AddMinutes(5),
                cancellationToken)
            .Where(IsGeneralCodexQuotaSnapshot);
        if (supplemental is not null)
        {
            snapshots = snapshots.Append(supplemental);
        }

        return CodexQuotaCycleReader.MarkTransientResetOutliers(snapshots, cancellationToken)
            .Where(item => !item.IsAnomaly)
            .OrderByDescending(item => item.SnapshotLocal)
            .FirstOrDefault();
    }

    private static bool HasBackwardWeeklyReset(
        CodexQuotaEstimate? establishedQuota,
        CodexQuotaSnapshot candidate)
    {
        return establishedQuota?.Week?.ResetAtLocal is { } establishedReset &&
               candidate.WeekResetAtLocal is { } candidateReset &&
               candidateReset < establishedReset.AddMinutes(-10);
    }

    public IReadOnlyList<CodexQuotaSnapshot> ReadQuotaSnapshots(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal)
    {
        return ReadCachedAndHistoricalQuotaSnapshots(startLocal, endLocal)
            .Where(IsGeneralCodexQuotaSnapshot)
            .ToList();
    }

    public IReadOnlyList<CodexQuotaSnapshot> ReadCachedQuotaSnapshots(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return QuotaSnapshotCacheStore.Load(CacheFolder)
            .GetSnapshots(startLocal, endLocal, cancellationToken)
            .Select(NormalizeQuotaSnapshotWindows)
            .ToList();
    }

    public IReadOnlyList<CodexQuotaSnapshot> ReadMaterializedQuotaTimeline(
        IEnumerable<DateTimeOffset> anchors,
        IEnumerable<CodexQuotaSnapshot>? supplementalSnapshots = null,
        bool refreshExisting = false,
        CancellationToken cancellationToken = default)
    {
        return ReadQuotaTimeline(anchors, supplementalSnapshots, refreshExisting,
            persistMissing: true, cancellationToken);
    }

    /// <summary>
    /// Projects existing quota caches without persisting missing timeline
    /// anchors or scanning session logs. Cached analysis views can use this
    /// path without queuing behind source-log backfill.
    /// </summary>
    public IReadOnlyList<CodexQuotaSnapshot> ReadCachedQuotaTimeline(
        IEnumerable<DateTimeOffset> anchors,
        IEnumerable<CodexQuotaSnapshot>? supplementalSnapshots = null,
        CancellationToken cancellationToken = default)
    {
        return ReadQuotaTimeline(anchors, supplementalSnapshots, refreshExisting: false,
            persistMissing: false, cancellationToken);
    }

    private IReadOnlyList<CodexQuotaSnapshot> ReadQuotaTimeline(
        IEnumerable<DateTimeOffset> anchors,
        IEnumerable<CodexQuotaSnapshot>? supplementalSnapshots,
        bool refreshExisting,
        bool persistMissing,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedAnchors = anchors
            .Select(anchor => anchor.ToOffset(BeijingOffset))
            .Distinct()
            .OrderBy(anchor => anchor)
            .ToList();
        if (normalizedAnchors.Count == 0)
        {
            return Array.Empty<CodexQuotaSnapshot>();
        }

        var cache = QuotaSnapshotCacheStore.Load(CacheFolder);
        var cached = cache.GetTimelineSnapshots(
                normalizedAnchors[0],
                normalizedAnchors[^1].AddTicks(1),
                cancellationToken)
            .Select(NormalizeQuotaSnapshotWindows)
            .ToList();
        var cachedByAnchor = cached.ToDictionary(item => item.SnapshotLocal, item => item);
        var missingAnchors = refreshExisting
            ? normalizedAnchors
            : normalizedAnchors.Where(anchor => !cachedByAnchor.ContainsKey(anchor)).ToList();

        if (missingAnchors.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceStart = StartOfDay(missingAnchors[0]).AddDays(-1);
            var sourceEnd = StartOfDay(missingAnchors[^1]).AddDays(2);
            var sourceSnapshots = PrepareQuotaTimelineSources(
                ReadCachedAndHistoricalQuotaSnapshots(sourceStart, sourceEnd, cancellationToken)
                    .Concat(supplementalSnapshots ?? Array.Empty<CodexQuotaSnapshot>()),
                cancellationToken);
            var allIndex = new QuotaTimelineSnapshotIndex(sourceSnapshots);
            var fiveHourIndex = new QuotaTimelineSnapshotIndex(sourceSnapshots.Where(item => item.FiveHourUsedPercent is not null));
            var weekIndex = new QuotaTimelineSnapshotIndex(sourceSnapshots.Where(item => item.WeekUsedPercent is not null));
            var materialized = missingAnchors
                .Select(anchor =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return MaterializeQuotaPoint(anchor, allIndex, fiveHourIndex, weekIndex);
                })
                .ToList();
            if (persistMissing)
            {
                var sourceMap = materialized.ToDictionary(
                    item => item.Snapshot.SnapshotLocal,
                    item => (item.BeforeSnapshotLocal, item.AfterSnapshotLocal));
                cache.PutTimelineSnapshots(
                    materialized.Select(item => item.Snapshot).ToList(),
                    sourceMap,
                    cancellationToken);
            }

            foreach (var point in materialized)
            {
                cachedByAnchor[point.Snapshot.SnapshotLocal] = point.Snapshot;
            }
        }

        return normalizedAnchors
            .Where(cachedByAnchor.ContainsKey)
            .Select(anchor => cachedByAnchor[anchor])
            .ToList();
    }

    public IReadOnlyList<DateTimeOffset> GetIncompleteQuotaTimelineDays(
        DateTimeOffset startInclusive,
        DateTimeOffset endInclusive,
        CancellationToken cancellationToken = default)
    {
        return QuotaSnapshotCacheStore.Load(CacheFolder)
            .GetIncompleteTimelineDays(startInclusive, endInclusive, cancellationToken);
    }

    public void WarmQuotaTimelineDay(
        DateTimeOffset dayLocal,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dayStart = StartOfDay(dayLocal);
        var dayEnd = dayStart.AddDays(1);
        var rows = ReadCachedDetailRows(dayStart, dayEnd, cancellationToken);
        if (rows.Count == 0)
        {
            return;
        }

        _ = ReadMaterializedQuotaTimeline(
            rows.Select(row => row.StartLocal),
            cancellationToken: cancellationToken);
    }

    public IReadOnlyList<CodexQuotaSnapshot> ReadCachedAndHistoricalQuotaSnapshots(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cached = ReadCachedQuotaSnapshots(startLocal, endLocal, cancellationToken);
        var history = ReadQuotaHistoryQuotaSnapshots(startLocal, endLocal, cancellationToken);
        var historySparkTimes = history
            .Where(IsGpt53QuotaSnapshot)
            .Select(item => item.SnapshotLocal)
            .ToHashSet();

        return MergeQuotaSnapshots(cached
                .Where(item => !IsStaleGeneralSnapshotForSpark(item, historySparkTimes))
                .Concat(history))
            .ToList();
    }

    private IReadOnlyList<CodexQuotaSnapshot> PrepareQuotaTimelineSources(
        IEnumerable<CodexQuotaSnapshot> snapshots,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var filtered = MergeQuotaSnapshots(snapshots)
            .Where(IsGeneralCodexQuotaSnapshot)
            .OrderBy(item => item.SnapshotLocal)
            .ToList();
        if (filtered.Count == 0)
        {
            return filtered;
        }

        var useful = filtered.Where(HasTimelineQuotaUsage).ToList();
        if (useful.Count > 0)
        {
            var usefulIndex = new QuotaTimelineSnapshotIndex(useful);
            filtered = filtered
                .Where(snapshot =>
                    !IsZeroTimelineQuotaSnapshot(snapshot) ||
                    !usefulIndex.AnyNearby(snapshot.SnapshotLocal, TimeSpan.FromMinutes(10),
                        other => TimelineQuotaWindowsOverlap(snapshot, other)))
                .ToList();
        }

        cancellationToken.ThrowIfCancellationRequested();
        return CodexQuotaCycleReader.MarkTransientResetOutliers(filtered, cancellationToken)
            .Where(item => !item.IsAnomaly)
            .OrderBy(item => item.SnapshotLocal)
            .ToList();
    }

    private MaterializedQuotaPoint MaterializeQuotaPoint(
        DateTimeOffset anchor,
        QuotaTimelineSnapshotIndex allIndex,
        QuotaTimelineSnapshotIndex fiveHourIndex,
        QuotaTimelineSnapshotIndex weekIndex)
    {
        var (before, after, nearest) = allIndex.Find(anchor);
        if (nearest is not null && Math.Abs((nearest.SnapshotLocal - anchor).TotalMinutes) <= 2)
        {
            return new MaterializedQuotaPoint(
                nearest with { SnapshotLocal = anchor, IsAnomaly = false },
                nearest.SnapshotLocal,
                nearest.SnapshotLocal);
        }

        var fiveHour = InterpolateTimelineWindow(
            anchor,
            fiveHourIndex,
            item => item.FiveHourUsedPercent,
            item => item.FiveHourResetAtLocal);
        var week = InterpolateTimelineWindow(
            anchor,
            weekIndex,
            item => item.WeekUsedPercent,
            item => item.WeekResetAtLocal);
        var identity = nearest ?? before ?? after;
        return new MaterializedQuotaPoint(
            new CodexQuotaSnapshot(
                anchor,
                identity?.LimitId,
                identity?.LimitName,
                fiveHour.UsedPercent,
                fiveHour.ResetAtLocal,
                week.UsedPercent,
                week.ResetAtLocal),
            before?.SnapshotLocal,
            after?.SnapshotLocal);
    }

    private static (decimal? UsedPercent, DateTimeOffset? ResetAtLocal) InterpolateTimelineWindow(
        DateTimeOffset anchor,
        QuotaTimelineSnapshotIndex index,
        Func<CodexQuotaSnapshot, decimal?> usedSelector,
        Func<CodexQuotaSnapshot, DateTimeOffset?> resetSelector)
    {
        var (before, after, nearest) = index.Find(anchor);
        if (before is not null && after is not null)
        {
            var beforeUsed = usedSelector(before)!.Value;
            var afterUsed = usedSelector(after)!.Value;
            var beforeReset = resetSelector(before);
            var afterReset = resetSelector(after);
            if (before.SnapshotLocal == after.SnapshotLocal)
            {
                return (ClampTimelinePercent(beforeUsed), beforeReset ?? afterReset);
            }

            if (SameTimelineQuotaReset(beforeReset, afterReset) && afterUsed + 1m >= beforeUsed)
            {
                var ratio = (decimal)((anchor - before.SnapshotLocal).TotalSeconds /
                                      (after.SnapshotLocal - before.SnapshotLocal).TotalSeconds);
                return (
                    ClampTimelinePercent(beforeUsed + ((afterUsed - beforeUsed) * ratio)),
                    afterReset ?? beforeReset);
            }
        }

        return nearest is not null && Math.Abs((nearest.SnapshotLocal - anchor).TotalMinutes) <= 10
            ? (ClampTimelinePercent(usedSelector(nearest)!.Value), resetSelector(nearest))
            : (null, null);
    }

    private static bool IsZeroTimelineQuotaSnapshot(CodexQuotaSnapshot snapshot)
    {
        return snapshot.FiveHourUsedPercent == 0m && snapshot.WeekUsedPercent == 0m;
    }

    private static bool HasTimelineQuotaUsage(CodexQuotaSnapshot snapshot)
    {
        return (snapshot.FiveHourUsedPercent ?? 0m) > 0m ||
               (snapshot.WeekUsedPercent ?? 0m) > 0m;
    }

    private static bool TimelineQuotaWindowsOverlap(CodexQuotaSnapshot first, CodexQuotaSnapshot second)
    {
        return SameTimelineQuotaReset(first.FiveHourResetAtLocal, second.FiveHourResetAtLocal) ||
               SameTimelineQuotaReset(first.WeekResetAtLocal, second.WeekResetAtLocal);
    }

    private static bool SameTimelineQuotaReset(DateTimeOffset? first, DateTimeOffset? second)
    {
        return first is not null && second is not null &&
               Math.Abs((first.Value - second.Value).TotalMinutes) <= 10;
    }

    private static decimal ClampTimelinePercent(decimal value)
    {
        return Math.Max(0m, Math.Min(100m, value));
    }

    public IReadOnlyList<CodexQuotaSnapshot> ReadQuotaHistoryQuotaSnapshots(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        CancellationToken cancellationToken = default)
    {
        var historySnapshots = ReadQuotaHistorySnapshots(startLocal, endLocal, cancellationToken)
            .Where(IsQuotaHistorySnapshot)
            .ToList();
        var historySparkTimes = historySnapshots
            .Where(IsGpt53QuotaSnapshot)
            .Select(item => item.TimestampLocal)
            .ToHashSet();

        return MergeQuotaSnapshots(historySnapshots
                .Where(item => !IsStaleGeneralSnapshotForLiveSpark(item, historySparkTimes))
                .Select(ToCodexQuotaSnapshot))
            .ToList();
    }

    public IReadOnlyList<DateTimeOffset> GetIncompleteQuotaSnapshotDays(
        DateTimeOffset startInclusive,
        DateTimeOffset endInclusive,
        CancellationToken cancellationToken = default)
    {
        return QuotaSnapshotCacheStore.GetIncompleteDays(
            CacheFolder,
            startInclusive,
            endInclusive,
            cancellationToken);
    }

    public void WarmQuotaSnapshotDay(
        DateTimeOffset dayLocal,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dayStart = StartOfDay(dayLocal);
        var dayEnd = dayStart.AddDays(1);
        var now = DateTimeOffset.UtcNow.ToOffset(BeijingOffset);
        if (dayStart <= now && dayEnd > now)
        {
            dayEnd = now;
        }

        _ = ReadQuotaSnapshotsCached(dayStart, dayEnd, cancellationToken);
        WarmQuotaTimelineDay(dayStart.AddDays(-1), cancellationToken);
        WarmQuotaTimelineDay(dayStart, cancellationToken);
        WarmQuotaTimelineDay(dayStart.AddDays(1), cancellationToken);
    }

    public void WarmQuotaSnapshotDays(
        IEnumerable<DateTimeOffset> daysLocal,
        CancellationToken cancellationToken = default,
        Action<DateTimeOffset>? dayCompleted = null,
        Action<int, int>? fileProgress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var today = StartOfDay(BeijingClock.Now);
        var days = daysLocal
            .Select(StartOfDay)
            .Where(day => day < today)
            .Distinct()
            .OrderBy(item => item)
            .ToList();
        if (days.Count == 0)
        {
            return;
        }

        var incomplete = GetIncompleteQuotaSnapshotDays(days[0], days[^1], cancellationToken).ToHashSet();
        foreach (var day in days.Where(day => !incomplete.Contains(day)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            dayCompleted?.Invoke(day);
        }
        days = days.Where(incomplete.Contains).ToList();
        if (days.Count == 0)
        {
            return;
        }

        var startLocal = days[0];
        var endLocal = days[^1].AddDays(1);
        var requestedDates = days
            .Select(item => DateOnly.FromDateTime(item.DateTime))
            .ToHashSet();

        cancellationToken.ThrowIfCancellationRequested();
        var liveScan = ReadRateLimitSnapshots(startLocal, endLocal, cancellationToken, fileProgress);
        var liveSnapshots = liveScan.Snapshots;
        var liveQuotaSnapshots = liveSnapshots
            .Where(IsQuotaHistorySnapshot)
            .Select(ToCodexQuotaSnapshot)
            .ToList();
        var liveSparkTimes = liveQuotaSnapshots
            .Where(IsGpt53QuotaSnapshot)
            .Select(item => item.SnapshotLocal)
            .ToHashSet();
        var historyQuotaSnapshots = ReadQuotaHistorySnapshots(startLocal, endLocal, cancellationToken)
            .Where(IsQuotaHistorySnapshot)
            .Where(item => !IsStaleGeneralSnapshotForLiveSpark(item, liveSparkTimes))
            .Select(ToCodexQuotaSnapshot);
        var scannedByDate = MergeQuotaSnapshots(liveQuotaSnapshots.Concat(historyQuotaSnapshots))
            .Where(item => requestedDates.Contains(DateOnly.FromDateTime(item.SnapshotLocal.DateTime)))
            .GroupBy(item => DateOnly.FromDateTime(item.SnapshotLocal.DateTime))
            .ToDictionary(group => group.Key, group => (IReadOnlyList<CodexQuotaSnapshot>)group.ToList());

        var cache = QuotaSnapshotCacheStore.Load(CacheFolder);
        foreach (var day in days.OrderByDescending(day => day))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var date = DateOnly.FromDateTime(day.DateTime);
            var existing = cache.GetSnapshots(date, cancellationToken);
            scannedByDate.TryGetValue(date, out var scanned);
            var merged = MergeQuotaSnapshots(existing.Concat(scanned ?? Array.Empty<CodexQuotaSnapshot>())).ToList();
            cache.Put(
                date,
                merged,
                isComplete: liveScan.IsComplete,
                scannedThroughLocal: day.AddDays(1).AddTicks(-1),
                cancellationToken: cancellationToken,
                propagateErrors: true);
            if (liveScan.IsComplete && cache.TryGetRecord(date, out var record) && record.IsComplete)
            {
                dayCompleted?.Invoke(day);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        cache.Save();
        CodexQuotaCycleReader.InvalidateCache();
    }

    private IReadOnlyList<CodexQuotaSnapshot> ReadQuotaSnapshotsCached(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        CancellationToken cancellationToken = default)
    {
        var result = new List<CodexQuotaSnapshot>();
        var cache = QuotaSnapshotCacheStore.Load(CacheFolder);
        var cacheChanged = false;
        var now = DateTimeOffset.UtcNow.ToOffset(BeijingOffset);
        var todayStart = StartOfDay(now);

        for (var dayStart = StartOfDay(startLocal); dayStart < endLocal; dayStart = dayStart.AddDays(1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dayEnd = dayStart.AddDays(1);
            var clippedStart = Max(dayStart, startLocal);
            var clippedEnd = Min(dayEnd, endLocal);
            if (clippedStart >= clippedEnd)
            {
                continue;
            }

            var date = DateOnly.FromDateTime(dayStart.DateTime);
            var daySnapshots = cache.GetSnapshots(date, cancellationToken).ToList();
            var liveToday = dayStart == todayStart;
            var effectiveClippedEnd = liveToday ? Min(clippedEnd, now) : clippedEnd;
            if (clippedStart >= effectiveClippedEnd)
            {
                continue;
            }

            var hasRecord = cache.TryGetRecord(date, out var record);
            var effectiveScannedThrough = hasRecord && liveToday
                ? GetEffectiveQuotaScannedThrough(record, now)
                : record?.ScannedThroughLocal;
            var hasPrefixGap = HasQuotaPrefixGap(clippedStart, daySnapshots);
            var hasAmbiguousQuotaCache = HasAmbiguousCodexQuotaCache(daySnapshots);
            var fullHistoricalDay = dayStart < todayStart;
            var hasCompleteCoverage =
                hasRecord && record is not null &&
                (fullHistoricalDay && record.IsComplete && record.IsValid ||
                 daySnapshots.Count > 0 &&
                 !hasPrefixGap &&
                 !hasAmbiguousQuotaCache &&
                 (record.IsComplete ||
                  effectiveScannedThrough is not null && effectiveScannedThrough.Value >= effectiveClippedEnd.AddTicks(-1)));

            if (!hasCompleteCoverage)
            {
                DateTimeOffset scanStart;
                if (hasPrefixGap)
                {
                    scanStart = clippedStart;
                }
                else if (hasAmbiguousQuotaCache)
                {
                    scanStart = dayStart;
                }
                else if (fullHistoricalDay)
                {
                    scanStart = dayStart;
                }
                else
                {
                    scanStart = effectiveScannedThrough is null
                        ? clippedStart
                        : Max(clippedStart, effectiveScannedThrough.Value.AddTicks(1));
                }

                var scanEnd = fullHistoricalDay ? dayEnd : effectiveClippedEnd;
                if (scanStart < scanEnd)
                {
                    var scannedResult = ReadQuotaSnapshotsUncached(scanStart, scanEnd, cancellationToken);
                    daySnapshots = MergeQuotaSnapshots(daySnapshots
                        .Concat(scannedResult.Snapshots)
                        .Where(item => item.SnapshotLocal >= dayStart && item.SnapshotLocal < dayEnd))
                        .ToList();
                    cache.Put(
                        date,
                        daySnapshots,
                        isComplete: fullHistoricalDay && scannedResult.IsComplete,
                        scannedThroughLocal: scanEnd.AddTicks(-1),
                        cancellationToken: cancellationToken);
                    cacheChanged = true;
                }
            }

            result.AddRange(daySnapshots.Where(item => item.SnapshotLocal >= clippedStart && item.SnapshotLocal < clippedEnd));
        }

        if (cacheChanged)
        {
            cache.Save();
        }

        return MergeQuotaSnapshots(result).ToList();
    }

    private static bool HasQuotaPrefixGap(
        DateTimeOffset clippedStart,
        IReadOnlyList<CodexQuotaSnapshot> daySnapshots)
    {
        if (daySnapshots.Count == 0)
        {
            return false;
        }

        var firstSnapshot = daySnapshots.Min(item => item.SnapshotLocal);
        return firstSnapshot > clippedStart.AddMinutes(5);
    }

    private static bool HasAmbiguousCodexQuotaCache(IReadOnlyList<CodexQuotaSnapshot> daySnapshots)
    {
        var resetGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var snapshot in daySnapshots)
        {
            if (!string.Equals(snapshot.LimitId, "codex", StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrWhiteSpace(snapshot.LimitName) ||
                snapshot.WeekResetAtLocal is not { } resetAt)
            {
                continue;
            }

            resetGroups.Add(resetAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
            if (resetGroups.Count > 1)
            {
                return true;
            }
        }

        return false;
    }

    private static DateTimeOffset? GetEffectiveQuotaScannedThrough(CachedQuotaDayRecord record, DateTimeOffset now)
    {
        if (record.ScannedThroughLocal is not { } scannedThrough)
        {
            return null;
        }

        if (scannedThrough <= now)
        {
            return scannedThrough;
        }

        return record.Snapshots
            .Where(item => item.SnapshotLocal <= now)
            .OrderByDescending(item => item.SnapshotLocal)
            .FirstOrDefault()
            ?.SnapshotLocal;
    }

    private QuotaSnapshotScanResult ReadQuotaSnapshotsUncached(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var liveScan = ReadRateLimitSnapshots(startLocal, endLocal, cancellationToken);
        foreach (var item in liveScan.Snapshots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AppendQuotaHistoryIfNew(item);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var liveQuotaSnapshots = liveScan.Snapshots
            .Where(IsQuotaHistorySnapshot)
            .Select(ToCodexQuotaSnapshot)
            .ToList();
        var liveSparkTimes = liveQuotaSnapshots
            .Where(IsGpt53QuotaSnapshot)
            .Select(item => item.SnapshotLocal)
            .ToHashSet();
        var historyQuotaSnapshots = ReadQuotaHistorySnapshots(startLocal, endLocal, cancellationToken)
            .Where(IsQuotaHistorySnapshot)
            .Where(item => !IsStaleGeneralSnapshotForLiveSpark(item, liveSparkTimes))
            .Select(ToCodexQuotaSnapshot);

        return new QuotaSnapshotScanResult(
            MergeQuotaSnapshots(liveQuotaSnapshots.Concat(historyQuotaSnapshots)).ToList(),
            liveScan.IsComplete);
    }

    private static bool IsStaleGeneralSnapshotForLiveSpark(
        RateLimitSnapshot snapshot,
        ISet<DateTimeOffset> liveSparkTimes)
    {
        return liveSparkTimes.Contains(snapshot.TimestampLocal) &&
               string.Equals(snapshot.LimitId, "codex", StringComparison.OrdinalIgnoreCase) &&
               string.IsNullOrWhiteSpace(snapshot.LimitName);
    }

    private static bool IsStaleGeneralSnapshotForSpark(
        CodexQuotaSnapshot snapshot,
        ISet<DateTimeOffset> sparkTimes)
    {
        return sparkTimes.Contains(snapshot.SnapshotLocal) &&
               string.Equals(snapshot.LimitId, "codex", StringComparison.OrdinalIgnoreCase) &&
               string.IsNullOrWhiteSpace(snapshot.LimitName);
    }

    private static IEnumerable<CodexQuotaSnapshot> MergeQuotaSnapshots(IEnumerable<CodexQuotaSnapshot> snapshots)
    {
        return snapshots
            .Select(NormalizeQuotaSnapshotWindows)
            .GroupBy(item => $"{item.SnapshotLocal:O}|{NormalizeLimitId(item.LimitId)}", StringComparer.OrdinalIgnoreCase)
            .Select(SelectBestQuotaSnapshot)
            .OrderBy(item => item.SnapshotLocal);
    }

    internal static CodexQuotaSnapshot NormalizeQuotaSnapshotWindows(CodexQuotaSnapshot snapshot)
    {
        var normalizedFiveHour = QuotaPercentRules.Normalize(snapshot.FiveHourUsedPercent);
        var normalizedWeek = QuotaPercentRules.Normalize(snapshot.WeekUsedPercent);
        if (normalizedFiveHour != snapshot.FiveHourUsedPercent ||
            normalizedWeek != snapshot.WeekUsedPercent)
        {
            snapshot = snapshot with
            {
                FiveHourUsedPercent = normalizedFiveHour,
                FiveHourResetAtLocal = normalizedFiveHour is null ? null : snapshot.FiveHourResetAtLocal,
                WeekUsedPercent = normalizedWeek,
                WeekResetAtLocal = normalizedWeek is null ? null : snapshot.WeekResetAtLocal
            };
        }

        // During the temporary removal of the 5h limit, Codex emits the 7d
        // window as `primary` and omits `secondary`. Older builds persisted
        // that payload in the 5h columns. A real 5h reset cannot be more than
        // one day after its snapshot, so repair those cached rows on read.
        if (snapshot.FiveHourUsedPercent is not null &&
            snapshot.WeekUsedPercent is null &&
            snapshot.FiveHourResetAtLocal is { } resetAt)
        {
            var resetDistance = resetAt - snapshot.SnapshotLocal;
            if (resetDistance > TimeSpan.FromDays(1) && resetDistance <= TimeSpan.FromDays(8))
            {
                snapshot = snapshot with
                {
                    FiveHourUsedPercent = null,
                    FiveHourResetAtLocal = null,
                    WeekUsedPercent = snapshot.FiveHourUsedPercent,
                    WeekResetAtLocal = resetAt
                };
            }
            else if (resetDistance > TimeSpan.FromDays(8))
            {
                snapshot = snapshot with
                {
                    FiveHourUsedPercent = null,
                    FiveHourResetAtLocal = null
                };
            }
        }

        if (snapshot.WeekResetAtLocal is { } weekReset &&
            (weekReset < snapshot.SnapshotLocal.AddMinutes(-10) ||
             weekReset - snapshot.SnapshotLocal > TimeSpan.FromDays(8)))
        {
            snapshot = snapshot with
            {
                WeekUsedPercent = null,
                WeekResetAtLocal = null
            };
        }

        return snapshot;
    }

    private static CodexQuotaSnapshot SelectBestQuotaSnapshot(IEnumerable<CodexQuotaSnapshot> snapshots)
    {
        return snapshots
            .OrderByDescending(item => item.WeekUsedPercent ?? -1m)
            .ThenByDescending(item => item.FiveHourUsedPercent ?? -1m)
            .ThenByDescending(GetQuotaSnapshotCompleteness)
            .ThenByDescending(item => item.SnapshotLocal)
            .First();
    }

    private static int GetQuotaSnapshotCompleteness(CodexQuotaSnapshot snapshot)
    {
        var score = 0;
        if (!string.IsNullOrWhiteSpace(snapshot.LimitId))
        {
            score++;
        }

        if (!string.IsNullOrWhiteSpace(snapshot.LimitName))
        {
            score++;
        }

        if (snapshot.FiveHourUsedPercent is not null)
        {
            score++;
        }

        if (snapshot.FiveHourResetAtLocal is not null)
        {
            score++;
        }

        if (snapshot.WeekUsedPercent is not null)
        {
            score++;
        }

        if (snapshot.WeekResetAtLocal is not null)
        {
            score++;
        }

        return score;
    }

    private CodexQuotaEstimate BuildQuotaEstimate(
        RateLimitSnapshot snapshot,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        return new CodexQuotaEstimate(
            snapshot.TimestampLocal,
            snapshot.LimitId,
            snapshot.LimitName,
            BuildQuotaWindowEstimate("5h", snapshot.FiveHour, now, cancellationToken: cancellationToken),
            BuildQuotaWindowEstimate("1周", snapshot.Week, now, cancellationToken: cancellationToken));
    }

    private CodexQuotaEstimate BuildQuotaEstimate(
        CodexQuotaSnapshot snapshot,
        DateTimeOffset now,
        bool includeLiveToday = true,
        CancellationToken cancellationToken = default)
    {
        return new CodexQuotaEstimate(
            snapshot.SnapshotLocal,
            snapshot.LimitId,
            snapshot.LimitName,
            BuildQuotaWindowEstimate(
                "5h",
                ToRateLimitWindow(snapshot.FiveHourUsedPercent, 5 * 60, snapshot.FiveHourResetAtLocal),
                now,
                includeLiveToday,
                cancellationToken),
            BuildQuotaWindowEstimate(
                "1周",
                ToRateLimitWindow(snapshot.WeekUsedPercent, 7 * 24 * 60, snapshot.WeekResetAtLocal),
                now,
                includeLiveToday,
                cancellationToken));
    }

    private static RateLimitWindowSnapshot? ToRateLimitWindow(
        decimal? usedPercent,
        int windowMinutes,
        DateTimeOffset? resetAtLocal)
    {
        return usedPercent is null
            ? null
            : new RateLimitWindowSnapshot(usedPercent.Value, windowMinutes, resetAtLocal);
    }

    private CodexQuotaWindowEstimate? BuildQuotaWindowEstimate(
        string label,
        RateLimitWindowSnapshot? snapshot,
        DateTimeOffset now,
        bool includeLiveToday = true,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (snapshot is not { } window || window.WindowMinutes <= 0)
        {
            return null;
        }

        var windowStart = window.ResetAtLocal is null
            ? now.AddMinutes(-window.WindowMinutes)
            : window.ResetAtLocal.Value.AddMinutes(-window.WindowMinutes);
        if (windowStart > now)
        {
            windowStart = now.AddMinutes(-window.WindowMinutes);
        }

        var usage = ReadRangeFromDetailRows(windowStart, now, includeLiveToday, cancellationToken);
        var usedCost = CodexModelCost.Estimate(usage).KnownCost;
        var estimatedCostLimit = CodexModelCost.EstimateQuotaValue(usage, window.UsedPercent);
        // A mixed-model subscription does not have a fixed token capacity.
        long? estimatedTokenLimit = null;
        return new CodexQuotaWindowEstimate(
            label,
            window.UsedPercent,
            window.WindowMinutes,
            windowStart,
            now,
            window.ResetAtLocal,
            usage,
            usedCost,
            estimatedCostLimit,
            estimatedTokenLimit);
    }

    public TokenUsageSummary ReadRangeFromDetailRows(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        bool includeLiveToday = true,
        CancellationToken cancellationToken = default)
    {
        startLocal = startLocal.ToOffset(BeijingOffset);
        endLocal = endLocal.ToOffset(BeijingOffset);
        var summary = new TokenUsageSummary
        {
            StartLocal = startLocal,
            EndLocal = endLocal
        };
        var dailyBuckets = new Dictionary<DateOnly, TokenUsageBucket>();

        for (var segmentStart = startLocal; segmentStart < endLocal;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var nextDay = StartOfDay(segmentStart).AddDays(1);
            var segmentEnd = nextDay < endLocal ? nextDay : endLocal;
            var rows = includeLiveToday
                ? ReadDetailRows(
                    segmentStart,
                    segmentEnd,
                    includeLiveToday: true,
                    cancellationToken: cancellationToken)
                : ReadCachedDetailRows(segmentStart, segmentEnd, cancellationToken);
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AddBucketToSummary(summary, dailyBuckets, row);
            }

            segmentStart = segmentEnd;
        }

        summary.DailyBuckets.AddRange(
            dailyBuckets.Values
                .OrderBy(bucket => bucket.StartLocal)
                .Where(bucket => bucket.Events > 0));

        return summary;
    }

    private void AppendQuotaHistoryIfNew(RateLimitSnapshot snapshot)
    {
        try
        {
            var path = GetQuotaHistoryPath();
            var historyKey = new QuotaHistoryKey(snapshot.TimestampLocal, NormalizeLimitId(snapshot.LimitId));
            lock (QuotaHistoryCacheSync)
            {
                EnsureQuotaHistoryCacheLoaded(path);
                if (QuotaHistoryKeyCache.Contains(historyKey))
                {
                    return;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var line = JsonSerializer.Serialize(new
                {
                    snapshotLocal = snapshot.TimestampLocal,
                    limitId = snapshot.LimitId,
                    limitName = snapshot.LimitName,
                    modelContextWindow = snapshot.ModelContextWindow,
                    fiveHour = ToQuotaHistoryWindow(snapshot.FiveHour),
                    week = ToQuotaHistoryWindow(snapshot.Week)
                });
                File.AppendAllText(path, line + Environment.NewLine);
                QuotaHistorySnapshotCache.Add(snapshot);
                QuotaHistoryKeyCache.Add(historyKey);
                UpdateQuotaHistoryCacheFileState(path);
            }
        }
        catch
        {
            // Quota history is best-effort; the live estimate should still render if persistence fails.
        }
    }

    private static object? ToQuotaHistoryWindow(RateLimitWindowSnapshot? window)
    {
        if (window is not { } value)
        {
            return null;
        }

        return new
        {
            usedPercent = value.UsedPercent,
            windowMinutes = value.WindowMinutes,
            resetAtLocal = value.ResetAtLocal
        };
    }

    private void EnsureQuotaHistoryCacheLoaded(
        string path,
        CancellationToken cancellationToken = default)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            ResetQuotaHistoryCache(path);
            return;
        }

        if (string.Equals(quotaHistoryCachedPath, path, StringComparison.OrdinalIgnoreCase) &&
            quotaHistoryCachedLength == info.Length &&
            quotaHistoryCachedWriteTimeUtc == info.LastWriteTimeUtc)
        {
            return;
        }

        QuotaHistorySnapshotCache.Clear();
        QuotaHistoryKeyCache.Clear();
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(line) || TryReadQuotaHistorySnapshot(line) is not { } snapshot)
                {
                    continue;
                }

                QuotaHistorySnapshotCache.Add(snapshot);
                QuotaHistoryKeyCache.Add(new QuotaHistoryKey(
                    snapshot.TimestampLocal,
                    NormalizeLimitId(snapshot.LimitId)));
            }

            UpdateQuotaHistoryCacheFileState(path);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ResetQuotaHistoryCache(path);
            throw;
        }
    }

    private void ResetQuotaHistoryCache(string path)
    {
        QuotaHistorySnapshotCache.Clear();
        QuotaHistoryKeyCache.Clear();
        quotaHistoryCachedPath = path;
        quotaHistoryCachedLength = -1;
        quotaHistoryCachedWriteTimeUtc = default;
    }

    private void UpdateQuotaHistoryCacheFileState(string path)
    {
        var info = new FileInfo(path);
        info.Refresh();
        quotaHistoryCachedPath = path;
        quotaHistoryCachedLength = info.Exists ? info.Length : -1;
        quotaHistoryCachedWriteTimeUtc = info.Exists ? info.LastWriteTimeUtc : default;
    }

    private static string NormalizeLimitId(string? limitId)
    {
        return string.IsNullOrWhiteSpace(limitId)
            ? "unknown"
            : limitId.Trim();
    }

    private string GetQuotaHistoryPath()
    {
        return Path.Combine(MonitorCachePaths.LocalAppData, CacheFolder, QuotaHistoryFileName);
    }

    private IReadOnlyList<RateLimitSnapshot> ReadQuotaHistorySnapshots(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        CancellationToken cancellationToken = default)
    {
        var path = GetQuotaHistoryPath();
        lock (QuotaHistoryCacheSync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureQuotaHistoryCacheLoaded(path, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return QuotaHistorySnapshotCache
                .Where(snapshot => snapshot.TimestampLocal >= startLocal && snapshot.TimestampLocal < endLocal)
                .ToList();
        }
    }

    private static RateLimitSnapshot? TryReadQuotaHistorySnapshot(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("snapshotLocal", out var snapshotElement) ||
                !DateTimeOffset.TryParse(snapshotElement.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var timestamp))
            {
                return null;
            }

            var limitId = GetString(root, "limitId");
            var limitName = GetString(root, "limitName");
            var modelContextWindow = GetInt64(root, "modelContextWindow");
            NormalizeRateLimitIdentity(modelContextWindow, ref limitId, ref limitName);

            var windows = ClassifyRateLimitWindows(
                TryReadQuotaHistoryWindow(root, "fiveHour"),
                TryReadQuotaHistoryWindow(root, "week"));

            return new RateLimitSnapshot(
                timestamp,
                limitId,
                limitName,
                windows.FiveHour,
                windows.Week,
                modelContextWindow);
        }
        catch
        {
            return null;
        }
    }

    private static RateLimitWindowSnapshot? TryReadQuotaHistoryWindow(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var window) ||
            window.ValueKind is JsonValueKind.Null)
        {
            return null;
        }

        var usedPercent = GetDecimal(window, "usedPercent");
        var windowMinutes = (int)GetInt64(window, "windowMinutes");
        if (!QuotaPercentRules.IsValid(usedPercent) || windowMinutes <= 0)
        {
            return null;
        }

        DateTimeOffset? resetAt = null;
        if (window.TryGetProperty("resetAtLocal", out var resetElement) &&
            resetElement.ValueKind is JsonValueKind.String &&
            DateTimeOffset.TryParse(resetElement.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsedReset))
        {
            resetAt = parsedReset;
        }

        return new RateLimitWindowSnapshot(usedPercent!.Value, windowMinutes, resetAt);
    }

    public void WarmHistoricalDays(
        IEnumerable<DateTimeOffset> daysLocal,
        CancellationToken cancellationToken = default,
        Action<DateTimeOffset>? dayCompleted = null,
        Action<int, int>? fileProgress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var todayStart = StartOfDay(DateTimeOffset.UtcNow.ToOffset(BeijingOffset));
        var days = daysLocal
            .Select(day => StartOfDay(day.ToOffset(BeijingOffset)))
            .Where(day => day < todayStart)
            .Distinct()
            .OrderByDescending(day => day)
            .ToList();
        if (days.Count == 0)
        {
            return;
        }

        var incompleteDates = GetIncompleteHistoricalDays(days[^1], days[0], cancellationToken)
            .Select(day => DateOnly.FromDateTime(day.DateTime))
            .ToHashSet();
        foreach (var day in days)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!incompleteDates.Contains(DateOnly.FromDateTime(day.DateTime)))
            {
                dayCompleted?.Invoke(day);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        days = days.Where(day => incompleteDates.Contains(DateOnly.FromDateTime(day.DateTime))).ToList();
        if (days.Count == 0)
        {
            return;
        }

        var startLocal = days[^1];
        var endLocal = days[0].AddDays(1);
        var eventsByDay = days.ToDictionary(
            day => DateOnly.FromDateTime(day.DateTime),
            _ => new List<TokenUsageEvent>());
        var files = new List<string>();
        foreach (var root in GetLogRoots())
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var file in EnumerateJsonlFiles(root, startLocal, endLocal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                files.Add(file);
            }
        }

        // Read each source once for the entire pending history. A historical
        // day's mtime filter also includes every later log, so scanning days
        // separately repeatedly reads the same (potentially very large) files.
        // Retain only token events for requested days, never the JSONL text.
        var isComplete = true;
        fileProgress?.Invoke(0, files.Count);
        for (var index = 0; index < files.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileEvents = new List<TokenUsageEvent>();
            if (!ReadEventFile(files[index], startLocal, endLocal, fileEvents, cancellationToken))
            {
                isComplete = false;
            }

            foreach (var item in fileEvents)
            {
                var date = DateOnly.FromDateTime(item.Timestamp.ToOffset(BeijingOffset).DateTime);
                if (eventsByDay.TryGetValue(date, out var dayEvents))
                {
                    dayEvents.Add(item);
                }
            }
            fileProgress?.Invoke(index + 1, files.Count);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var cache = UsageCacheStore.Load(CacheFolder);
        foreach (var dayStart in days)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var date = DateOnly.FromDateTime(dayStart.DateTime);
            // Deduplication is scoped to a day, matching usage_events' key and
            // the existing per-day reader when a turn continues past midnight.
            var events = UsageEventMerger.Merge(cache.GetDetailEvents(date, cancellationToken)
                .Concat(eventsByDay[date]));
            eventsByDay.Remove(date);
            var dayEnd = dayStart.AddDays(1);
            if (events.Count == 0 && cache.TryGet(date, out var legacyBucket) && legacyBucket.Events > 0)
            {
                // Keep legacy summary-only data if the original source is no
                // longer present, but do not claim its missing details are complete.
                cache.Put(legacyBucket, isComplete: false, scannedThroughLocal: dayEnd.AddTicks(-1),
                    cancellationToken: cancellationToken, propagateErrors: true);
                continue;
            }

            var bucket = CreateBucketFromEvents(dayStart, events);
            cache.Put(bucket, isComplete, dayEnd.AddTicks(-1), events,
                cancellationToken: cancellationToken, propagateErrors: true);
            cancellationToken.ThrowIfCancellationRequested();
            if (isComplete && GetIncompleteHistoricalDays(dayStart, dayStart, cancellationToken).Count == 0)
            {
                dayCompleted?.Invoke(dayStart);
            }
        }
    }

    public TokenUsageSummary ReadRange(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        bool includeLiveToday = true,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        startLocal = startLocal.ToOffset(BeijingOffset);
        endLocal = endLocal.ToOffset(BeijingOffset);
        var summary = new TokenUsageSummary
        {
            StartLocal = startLocal,
            EndLocal = endLocal
        };
        var dailyBuckets = new Dictionary<DateOnly, TokenUsageBucket>();
        var cache = UsageCacheStore.Load(CacheFolder);
        var cacheChanged = false;
        var scanRanges = new List<ScanRange>();

        var now = DateTimeOffset.UtcNow.ToOffset(BeijingOffset);
        var todayStart = StartOfDay(now);

        for (var dayStart = StartOfDay(startLocal); dayStart < endLocal; dayStart = dayStart.AddDays(1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dayEnd = dayStart.AddDays(1);
            var clippedStart = Max(dayStart, startLocal);
            var clippedEnd = Min(dayEnd, endLocal);
            if (clippedStart >= clippedEnd)
            {
                continue;
            }

            var fullHistoricalDay = clippedStart == dayStart && clippedEnd == dayEnd && dayStart < todayStart;
            var liveToday = dayStart == todayStart;
            var fullCachedDay = clippedStart == dayStart && (fullHistoricalDay || liveToday);
            var date = DateOnly.FromDateTime(dayStart.DateTime);

            // Quota cycles usually start partway through a day. A complete day
            // with all event details can answer that slice precisely, including
            // imported events. Reopening rollouts here both rescans history on
            // every cycle refresh and omits events that exist on another device.
            if (!fullHistoricalDay && dayStart < todayStart &&
                cache.TryGetRecord(date, out var boundaryRecord) &&
                boundaryRecord.IsComplete && boundaryRecord.DetailEventCount == boundaryRecord.Events)
            {
                var boundarySummary = cache.ReadRange(clippedStart, clippedEnd, cancellationToken);
                foreach (var bucket in boundarySummary.DailyBuckets)
                {
                    AddBucketToSummary(summary, dailyBuckets, bucket);
                }
                continue;
            }

            if (fullCachedDay && cache.TryGet(date, out var cachedBucket))
            {
                AddBucketToSummary(summary, dailyBuckets, cachedBucket);
            }

            if (fullHistoricalDay)
            {
                if (cache.TryGetRecord(date, out var record) &&
                    record.IsComplete &&
                    record.DetailEventCount == record.Events)
                {
                    continue;
                }

                // An incomplete historical record can retain an end-of-day
                // watermark after a schema change or a failed source scan.
                // Its previous coverage is no longer proof that the counters
                // are current, so rebuild the whole day and merge its details.
                AddScanRange(scanRanges, dayStart, dayEnd, cacheHistoricalDays: true);
            }
            else if (liveToday)
            {
                if (!includeLiveToday)
                {
                    continue;
                }

                var liveScanEnd = Min(clippedEnd, now);
                if (clippedStart >= liveScanEnd)
                {
                    continue;
                }

                var effectiveScannedThrough = cache.TryGetRecord(date, out var record)
                    ? GetEffectiveLiveScannedThrough(record, now)
                    : null;
                var scanStart = effectiveScannedThrough is not null
                    ? effectiveScannedThrough.Value.AddTicks(1)
                    : clippedStart;
                AddScanRange(scanRanges, Max(scanStart, clippedStart), liveScanEnd, cacheHistoricalDays: false);
            }
            else
            {
                AddScanRange(scanRanges, clippedStart, clippedEnd, cacheHistoricalDays: false);
            }
        }

        foreach (var scanRange in scanRanges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scanned = ReadRangeUncached(scanRange.StartLocal, scanRange.EndLocal, cancellationToken);
            foreach (var bucket in scanned.Summary.DailyBuckets)
            {
                AddBucketToSummary(summary, dailyBuckets, bucket);
            }

            for (var dayStart = StartOfDay(scanRange.StartLocal); dayStart < scanRange.EndLocal; dayStart = dayStart.AddDays(1))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var date = DateOnly.FromDateTime(dayStart.DateTime);
                var isToday = dayStart == todayStart;
                if (!scanRange.CacheHistoricalDays && !isToday)
                {
                    continue;
                }

                var scannedBucket = scanned.Summary.DailyBuckets.FirstOrDefault(item =>
                    DateOnly.FromDateTime(item.StartLocal.DateTime) == date) ?? new TokenUsageBucket
                    {
                        StartLocal = new DateTimeOffset(date.Year, date.Month, date.Day, 0, 0, 0, BeijingOffset)
                    };

                var mergedBucket = new TokenUsageBucket { StartLocal = scannedBucket.StartLocal };
                if (cache.TryGet(date, out var existingBucket))
                {
                    AddBucketValues(mergedBucket, existingBucket);
                }

                AddBucketValues(mergedBucket, scannedBucket);
                var scannedThrough = Min(dayStart.AddDays(1), scanRange.EndLocal).AddTicks(-1);
                var isComplete = scanRange.CacheHistoricalDays && dayStart < todayStart && scanned.IsComplete;
                IReadOnlyList<TokenUsageEvent>? detailEvents = null;
                var replaceDetailEvents = true;
                var hasDetailEvents = cache.HasDetailEvents(date, cancellationToken);
                if (hasDetailEvents)
                {
                    var detailStart = isToday
                        ? dayStart
                        : Max(dayStart, scanRange.StartLocal);
                    var detailEnd = Min(dayStart.AddDays(1), scanRange.EndLocal);
                    var newEventsResult = ReadEventsUncached(
                        detailStart,
                        detailEnd,
                        useLiveCursor: true,
                        cancellationToken: cancellationToken);
                    detailEvents = UsageEventMerger.Merge(cache.GetDetailEvents(date, cancellationToken)
                        .Concat(newEventsResult.Events)
                        .Where(item => item.Timestamp >= dayStart && item.Timestamp < dayStart.AddDays(1)));
                    mergedBucket = CreateBucketFromEvents(dayStart, detailEvents);
                    replaceDetailEvents = true;
                    isComplete &= newEventsResult.IsComplete;
                }
                else if (scanRange.CacheHistoricalDays && scannedBucket.Events > 0)
                {
                    // A legacy cache may contain only a daily aggregate. For
                    // a full historical rescan, prefer newly observed source
                    // events when available instead of adding them to the old
                    // aggregate and double-counting the day.
                    mergedBucket = scannedBucket;
                }

                cache.Put(
                    mergedBucket,
                    isComplete,
                    scannedThrough,
                    detailEvents,
                    replaceDetailEvents,
                    cancellationToken);
                dailyBuckets[date] = mergedBucket;
                cacheChanged = true;
            }
        }

        if (cacheChanged)
        {
            cancellationToken.ThrowIfCancellationRequested();
            cache.Save();
            summary = new TokenUsageSummary
            {
                StartLocal = startLocal,
                EndLocal = endLocal
            };
            foreach (var bucket in dailyBuckets.Values)
            {
                AddBucketValues(summary, bucket);
            }
        }

        summary.DailyBuckets.AddRange(
            dailyBuckets.Values
                .OrderBy(bucket => bucket.StartLocal)
                .Where(bucket => bucket.Events > 0));

        return summary;
    }

    public IReadOnlyList<TokenUsageBucket> ReadDetailRows(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        bool includeLiveToday = true,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        startLocal = startLocal.ToOffset(BeijingOffset);
        endLocal = endLocal.ToOffset(BeijingOffset);
        var dayStart = StartOfDay(startLocal);
        var date = DateOnly.FromDateTime(dayStart.DateTime);
        var now = DateTimeOffset.UtcNow.ToOffset(BeijingOffset);
        var todayStart = StartOfDay(now);
        var dayEnd = dayStart.AddDays(1);
        var effectiveEndLocal = dayStart == todayStart && includeLiveToday
            ? Min(endLocal, now)
            : endLocal;
        var cache = UsageCacheStore.Load(CacheFolder);
        var cachedEvents = cache.GetDetailEvents(date, cancellationToken).ToList();

        if (dayStart == todayStart && !includeLiveToday)
        {
            return ToDetailBuckets(cachedEvents.Where(item => item.Timestamp >= startLocal && item.Timestamp < endLocal));
        }

        if (cache.TryGetRecord(date, out var record) && (cachedEvents.Count > 0 || record.Events == 0))
        {
            var effectiveScannedThrough = dayStart == todayStart
                ? GetEffectiveLiveScannedThrough(record, now)
                : record.ScannedThroughLocal;
            var hasCompleteDetails = cachedEvents.Count == record.Events;
            var hasCompleteCoverage = hasCompleteDetails && (record.IsComplete ||
                                      dayStart == todayStart && effectiveScannedThrough is not null &&
                                      effectiveScannedThrough.Value >= effectiveEndLocal.AddTicks(-1));
            if (hasCompleteCoverage)
            {
                return ToDetailBuckets(cachedEvents.Where(item => item.Timestamp >= startLocal && item.Timestamp < endLocal));
            }

            if (dayStart < todayStart || includeLiveToday)
            {
                var replaceDetails = !hasCompleteDetails || dayStart < todayStart && !record.IsComplete;
                var scanStart = replaceDetails || effectiveScannedThrough is null
                    ? replaceDetails ? dayStart : startLocal
                    : Max(startLocal, effectiveScannedThrough.Value.AddTicks(1));
                var scanEnd = dayStart < todayStart ? dayEnd : effectiveEndLocal;
                if (scanStart < scanEnd)
                {
                    // Always give a fresh live cursor the full day. This self-heals a cache
                    // produced by an older build that advanced past future-dated JSONL rows.
                    var newEventsResult = ReadEventsUncached(
                        dayStart,
                        scanEnd,
                        useLiveCursor: true,
                        cancellationToken: cancellationToken);
                    var mergedEvents = UsageEventMerger.Merge(cachedEvents
                        .Concat(newEventsResult.Events)
                        .Where(item => item.Timestamp >= dayStart && item.Timestamp < dayEnd));
                    var mergedBucket = CreateBucketFromEvents(dayStart, mergedEvents);
                    var isComplete = dayStart < todayStart && scanEnd >= dayEnd && newEventsResult.IsComplete;
                    cache.Put(
                        mergedBucket,
                        isComplete,
                        scanEnd.AddTicks(-1),
                        replaceDetails ? mergedEvents : newEventsResult.Events,
                        replaceDetailEvents: replaceDetails,
                        cancellationToken: cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    cache.Save();
                    cachedEvents = mergedEvents.ToList();
                }
            }

            return ToDetailBuckets(cachedEvents.Where(item => item.Timestamp >= startLocal && item.Timestamp < endLocal));
        }

        var fullEventsResult = ReadEventsUncached(startLocal, endLocal, cancellationToken: cancellationToken);
        var fullEvents = UsageEventMerger.Merge(cachedEvents.Concat(fullEventsResult.Events));
        var fullBucket = CreateBucketFromEvents(dayStart, fullEvents);
        var completeHistoricalDay = dayStart < todayStart && startLocal == dayStart && endLocal >= dayEnd && fullEventsResult.IsComplete;
        if (startLocal == dayStart)
        {
            cache.Put(
                fullBucket,
                completeHistoricalDay,
                endLocal.AddTicks(-1),
                fullEvents,
                cancellationToken: cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            cache.Save();
        }

        return ToDetailBuckets(fullEvents);
    }

    private static DateTimeOffset? GetEffectiveLiveScannedThrough(CachedDayRecord record, DateTimeOffset now)
    {
        if (record.ScannedThroughLocal is not { } scannedThrough)
        {
            return null;
        }

        if (scannedThrough <= now)
        {
            return scannedThrough;
        }

        return record.LastTokenEventLocal is { } lastTokenEvent && lastTokenEvent <= now
            ? lastTokenEvent
            : null;
    }

    public IReadOnlyList<TokenUsageBucket> ReadTransientDetailRows(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        startLocal = startLocal.ToOffset(BeijingOffset);
        endLocal = endLocal.ToOffset(BeijingOffset);
        return ToDetailBuckets(ReadEventsUncached(startLocal, endLocal, cancellationToken: cancellationToken).Events);
    }

    private UsageRangeScanResult ReadRangeUncached(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        CancellationToken cancellationToken)
    {
        PruneLiveFileState(startLocal, endLocal);
        var summary = new TokenUsageSummary
        {
            StartLocal = startLocal,
            EndLocal = endLocal
        };
        var dailyBuckets = new Dictionary<DateOnly, TokenUsageBucket>();
        var isComplete = true;

        foreach (var root in GetLogRoots())
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var file in EnumerateJsonlFiles(root, startLocal, endLocal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!ReadFile(file, startLocal, endLocal, summary, dailyBuckets, cancellationToken))
                {
                    isComplete = false;
                }
            }
        }

        summary.DailyBuckets.AddRange(
            dailyBuckets.Values
                .OrderBy(bucket => bucket.StartLocal)
                .Where(bucket => bucket.Events > 0));

        return new UsageRangeScanResult(summary, isComplete);
    }

    private UsageEventScanResult ReadEventsUncached(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        bool useLiveCursor = false,
        CancellationToken cancellationToken = default)
    {
        var events = new List<TokenUsageEvent>();
        var isComplete = true;
        var incremental = useLiveCursor && IsLiveRange(startLocal, endLocal);
        if (incremental)
        {
            PruneLiveFileState(startLocal, endLocal);
        }

        foreach (var root in GetLogRoots())
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var file in EnumerateJsonlFiles(root, startLocal, endLocal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (incremental)
                {
                    if (!ReadEventFileIncremental(file, startLocal, endLocal, events, cancellationToken))
                    {
                        isComplete = false;
                    }
                }
                else
                {
                    if (!ReadEventFile(file, startLocal, endLocal, events, cancellationToken))
                    {
                        isComplete = false;
                    }
                }
            }
        }

        return new UsageEventScanResult(UsageEventMerger.Merge(events).ToList(), isComplete);
    }

    private bool ReadEventFile(
        string file,
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        List<TokenUsageEvent> events,
        CancellationToken cancellationToken)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var replayFilter = new SubagentReplayFilter();
            while (reader.ReadLine() is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!replayFilter.ShouldReadTokenCount(line))
                {
                    continue;
                }

                var usageEvent = TryReadUsageEvent(line, startLocal, endLocal, replayFilter.ModelId, replayFilter.ServiceTier);
                if (usageEvent is not null)
                {
                    events.Add(usageEvent);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }

        return true;
    }

    private bool ReadEventFileIncremental(
        string file,
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        List<TokenUsageEvent> events,
        CancellationToken cancellationToken)
    {
        var replayFilter = UsageReplayFilters.GetOrAdd(file, static _ => new SubagentReplayFilter());
        return UsageTailReader.ReadNewLinesWhile(file, startLocal, line =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (SubagentReplayFilter.TryReadRecordTimestamp(line) is { } timestamp && timestamp >= endLocal)
            {
                return false;
            }

            if (!replayFilter.ShouldReadTokenCount(line))
            {
                return true;
            }

            var usageEvent = TryReadUsageEvent(line, startLocal, endLocal, replayFilter.ModelId, replayFilter.ServiceTier);
            if (usageEvent is not null)
            {
                events.Add(usageEvent);
            }

            return true;
        }, cancellationToken, onRestart: () =>
        {
            replayFilter = new SubagentReplayFilter();
            UsageReplayFilters[file] = replayFilter;
        });
    }

    private static IReadOnlyList<TokenUsageBucket> ToDetailBuckets(IEnumerable<TokenUsageEvent> events)
    {
        return UsageEventMerger.Merge(events)
            .Select(item =>
            {
                var bucket = new TokenUsageBucket { StartLocal = item.Timestamp };
                bucket.Add(item);
                return bucket;
            })
            .ToList();
    }

    private static TokenUsageBucket CreateBucketFromEvents(
        DateTimeOffset bucketStart,
        IEnumerable<TokenUsageEvent> events)
    {
        var bucket = new TokenUsageBucket { StartLocal = bucketStart };
        foreach (var item in UsageEventMerger.Merge(events))
        {
            bucket.Add(item);
        }

        return bucket;
    }

    private void AddScanRange(
        List<ScanRange> scanRanges,
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        bool cacheHistoricalDays)
    {
        if (startLocal >= endLocal)
        {
            return;
        }

        if (scanRanges.Count > 0)
        {
            var previous = scanRanges[^1];
            if (previous.EndLocal == startLocal && previous.CacheHistoricalDays == cacheHistoricalDays)
            {
                scanRanges[^1] = previous with { EndLocal = endLocal };
                return;
            }
        }

        scanRanges.Add(new ScanRange(startLocal, endLocal, cacheHistoricalDays));
    }

    private static void AddBucketToSummary(
        TokenUsageSummary summary,
        Dictionary<DateOnly, TokenUsageBucket> dailyBuckets,
        TokenUsageBucket bucket)
    {
        if (bucket.Events == 0)
        {
            return;
        }

        AddBucketValues(summary, bucket);
        var dayKey = DateOnly.FromDateTime(bucket.StartLocal.DateTime);
        if (!dailyBuckets.TryGetValue(dayKey, out var dailyBucket))
        {
            dailyBucket = new TokenUsageBucket { StartLocal = bucket.StartLocal };
            dailyBuckets[dayKey] = dailyBucket;
        }

        AddBucketValues(dailyBucket, bucket);
    }

    private static void AddBucketValues(TokenUsageBucket target, TokenUsageBucket source)
    {
        target.MergeFrom(source);
    }

    private static DateTimeOffset StartOfDay(DateTimeOffset value)
    {
        var local = value.ToOffset(BeijingOffset);
        return new DateTimeOffset(local.Year, local.Month, local.Day, 0, 0, 0, BeijingOffset);
    }

    private static DateTimeOffset Max(DateTimeOffset first, DateTimeOffset second)
    {
        return first >= second ? first : second;
    }

    private static DateTimeOffset Min(DateTimeOffset first, DateTimeOffset second)
    {
        return first <= second ? first : second;
    }

    private static IEnumerable<string> GetLogRoots()
    {
        var codexHome = UsageLogPaths.GetOverrideRoot(UsageSource.Codex);
        if (string.IsNullOrWhiteSpace(codexHome))
        {
            codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        }
        if (string.IsNullOrWhiteSpace(codexHome))
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            codexHome = Path.Combine(profile, ".codex");
        }

        var sessions = Path.Combine(codexHome, "sessions");
        if (Directory.Exists(sessions))
        {
            yield return sessions;
        }

        var archived = Path.Combine(codexHome, "archived_sessions");
        if (Directory.Exists(archived))
        {
            yield return archived;
        }
    }

    private IEnumerable<string> EnumerateJsonlFiles(
        string root,
        DateTimeOffset startLocal,
        DateTimeOffset endLocal)
    {
        // Session contents can extend beyond their preserved file mtime (including
        // copied history). Historical batches inspect all files once; only live
        // tail polling uses mtime to narrow the candidates.
        var incremental = IsLiveRange(startLocal, endLocal);
        var startUtc = startLocal.Subtract(TimeSpan.FromDays(1)).UtcDateTime;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true
        };

        foreach (var file in Directory.EnumerateFiles(root, "*.jsonl", options))
        {
            if (!incremental)
            {
                yield return file;
                continue;
            }

            FileInfo info;
            try
            {
                info = new FileInfo(file);
            }
            catch
            {
                continue;
            }

            if (info.LastWriteTimeUtc >= startUtc ||
                UsageTailReader.IsTracked(file) ||
                QuotaTailReader.IsTracked(file))
            {
                yield return file;
            }
        }
    }

    private IReadOnlyList<RateLimitSnapshot> ReadLatestRateLimitSnapshots(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        CancellationToken cancellationToken = default)
    {
        return ReadRateLimitSnapshots(startLocal, endLocal, cancellationToken).Snapshots
            .GroupBy(GetRateLimitKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(item => item.TimestampLocal).First())
            .OrderByDescending(item => item.TimestampLocal)
            .ToList();
    }

    private RateLimitScanResult ReadRateLimitSnapshots(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        CancellationToken cancellationToken = default,
        Action<int, int>? fileProgress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshots = new List<RateLimitSnapshot>();
        var isComplete = true;
        var incremental = IsLiveRange(startLocal, endLocal);
        if (incremental)
        {
            PruneLiveFileState(startLocal, endLocal);
        }

        var files = GetLogRoots()
            .SelectMany(root => EnumerateJsonlFiles(root, startLocal, endLocal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        fileProgress?.Invoke(0, files.Count);
        var completed = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (incremental)
                {
                    if (!ReadRateLimitFileIncremental(file, startLocal, endLocal, snapshots, cancellationToken))
                    {
                        isComplete = false;
                    }
                    continue;
                }

                try
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream);
                    var replayFilter = new SubagentReplayFilter();
                    while (reader.ReadLine() is { } line)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!replayFilter.ShouldReadTokenCount(line) ||
                            !line.Contains("\"rate_limits\"", StringComparison.Ordinal))
                        {
                            continue;
                        }

                        if (TryReadRateLimitSnapshot(line, startLocal, endLocal) is not { } snapshot)
                        {
                            continue;
                        }

                        snapshots.Add(snapshot);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // Ignore files that are actively being written or are not readable.
                    isComplete = false;
                }
            }
            finally
            {
                fileProgress?.Invoke(++completed, files.Count);
            }
        }

        return new RateLimitScanResult(snapshots, isComplete);
    }

    private bool ReadRateLimitFileIncremental(
        string file,
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        List<RateLimitSnapshot> snapshots,
        CancellationToken cancellationToken)
    {
        var replayFilter = QuotaReplayFilters.GetOrAdd(file, static _ => new SubagentReplayFilter());
        return QuotaTailReader.ReadNewLinesWhile(file, startLocal, line =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (SubagentReplayFilter.TryReadRecordTimestamp(line) is { } timestamp && timestamp >= endLocal)
            {
                return false;
            }

            if (!replayFilter.ShouldReadTokenCount(line) ||
                !line.Contains("\"rate_limits\"", StringComparison.Ordinal))
            {
                return true;
            }

            if (TryReadRateLimitSnapshot(line, startLocal, endLocal) is { } snapshot)
            {
                snapshots.Add(snapshot);
            }

            return true;
        }, cancellationToken);
    }

    private static bool IsLiveRange(DateTimeOffset startLocal, DateTimeOffset endLocal)
    {
        var now = DateTimeOffset.UtcNow.ToOffset(BeijingOffset);
        return startLocal >= StartOfDay(now) && endLocal <= now.AddMinutes(10);
    }

    private void ResetLiveFileCursors()
    {
        UsageTailReader.Reset();
        QuotaTailReader.Reset();
        UsageReplayFilters.Clear();
        QuotaReplayFilters.Clear();
    }

    private void PruneLiveFileState(DateTimeOffset startLocal, DateTimeOffset endLocal)
    {
        if (!IsLiveRange(startLocal, endLocal))
        {
            return;
        }

        // Keep the same one-day look-back used by EnumerateJsonlFiles. A file
        // appended after this point will get a fresh mtime and be discovered on
        // the next pass, while inactive historical files no longer pin state.
        var cutoffUtc = startLocal.Subtract(TimeSpan.FromDays(1)).UtcDateTime;
        UsageTailReader.PruneBeforeUtc(cutoffUtc);
        QuotaTailReader.PruneBeforeUtc(cutoffUtc);
        PruneReplayFiltersBeforeUtc(UsageReplayFilters, cutoffUtc);
        PruneReplayFiltersBeforeUtc(QuotaReplayFilters, cutoffUtc);
    }

    private void PruneReplayFiltersBeforeUtc(
        ConcurrentDictionary<string, SubagentReplayFilter> filters,
        DateTime cutoffUtc)
    {
        foreach (var pair in filters)
        {
            bool shouldRemove;
            try
            {
                var info = new FileInfo(pair.Key);
                shouldRemove = !info.Exists || info.LastWriteTimeUtc < cutoffUtc;
            }
            catch
            {
                continue;
            }

            if (shouldRemove)
            {
                ((ICollection<KeyValuePair<string, SubagentReplayFilter>>)filters).Remove(pair);
            }
        }
    }

    private static RateLimitSnapshot? SelectDisplayedQuotaSnapshot(IReadOnlyList<RateLimitSnapshot> snapshots)
    {
        return snapshots
            .Where(IsDisplayedQuotaSnapshot)
            .OrderByDescending(item => item.TimestampLocal)
            .Select(item => (RateLimitSnapshot?)item)
            .FirstOrDefault();
    }

    internal static bool IsGeneralCodexQuotaSnapshot(CodexQuotaSnapshot snapshot)
    {
        return IsGeneralCodexQuota(snapshot.LimitId, snapshot.LimitName);
    }

    private static bool IsDisplayedQuotaSnapshot(RateLimitSnapshot snapshot)
    {
        return IsGeneralCodexQuota(snapshot.LimitId, snapshot.LimitName);
    }

    private static bool IsQuotaHistorySnapshot(RateLimitSnapshot snapshot)
    {
        return string.Equals(snapshot.LimitId, "codex", StringComparison.OrdinalIgnoreCase) ||
               ContainsIgnoreCase(snapshot.LimitId, "codex") ||
               ContainsIgnoreCase(snapshot.LimitName, "codex");
    }

    private static bool IsGpt53QuotaSnapshot(RateLimitSnapshot snapshot)
    {
        return IsGpt53QuotaSnapshot(snapshot.LimitId, snapshot.LimitName);
    }

    private static bool IsGpt53QuotaSnapshot(CodexQuotaSnapshot snapshot)
    {
        return IsGpt53QuotaSnapshot(snapshot.LimitId, snapshot.LimitName);
    }

    private static bool IsGeneralCodexQuota(string? limitId, string? limitName)
    {
        return !IsGpt53QuotaSnapshot(limitId, limitName) &&
               string.Equals(limitId, "codex", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGpt53QuotaSnapshot(string? limitId, string? limitName)
    {
        return ContainsIgnoreCase(limitId, "bengalfox") ||
               ContainsIgnoreCase(limitId, "gpt-5.3") ||
               ContainsIgnoreCase(limitName, "gpt-5.3") ||
               ContainsIgnoreCase(limitName, "spark");
    }

    private static void NormalizeRateLimitIdentity(long modelContextWindow, ref string? limitId, ref string? limitName)
    {
        if (IsGpt53QuotaSnapshot(limitId, limitName))
        {
            return;
        }

        if (modelContextWindow > 0 &&
            modelContextWindow <= SparkContextWindowUpperBound &&
            string.Equals(limitId, "codex", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(limitName))
        {
            limitId = SparkLimitId;
            limitName = SparkLimitName;
        }
    }

    private static string GetRateLimitKey(RateLimitSnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.LimitId))
        {
            return snapshot.LimitId;
        }

        if (!string.IsNullOrWhiteSpace(snapshot.LimitName))
        {
            return $"name:{snapshot.LimitName}";
        }

        return "unknown";
    }

    private static bool ContainsIgnoreCase(string? value, string needle)
    {
        return value?.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static CodexQuotaSnapshot ToCodexQuotaSnapshot(RateLimitSnapshot snapshot)
    {
        return new CodexQuotaSnapshot(
            snapshot.TimestampLocal,
            snapshot.LimitId,
            snapshot.LimitName,
            snapshot.FiveHour?.UsedPercent,
            snapshot.FiveHour?.ResetAtLocal,
            snapshot.Week?.UsedPercent,
            snapshot.Week?.ResetAtLocal);
    }

    private static RateLimitSnapshot ToRateLimitSnapshot(CodexQuotaSnapshot snapshot)
    {
        return new RateLimitSnapshot(
            snapshot.SnapshotLocal,
            snapshot.LimitId,
            snapshot.LimitName,
            ToRateLimitWindow(snapshot.FiveHourUsedPercent, FiveHourWindowMinutes, snapshot.FiveHourResetAtLocal),
            ToRateLimitWindow(snapshot.WeekUsedPercent, WeeklyWindowMinutes, snapshot.WeekResetAtLocal),
            0);
    }

    private static RateLimitSnapshot? TryReadRateLimitSnapshot(
        string line,
        DateTimeOffset startLocal,
        DateTimeOffset endLocal)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!StringEquals(root, "type", "event_msg") ||
                !root.TryGetProperty("timestamp", out var timestampElement))
            {
                return null;
            }

            var timestampText = timestampElement.GetString();
            if (string.IsNullOrWhiteSpace(timestampText))
            {
                return null;
            }

            var timestamp = DateTimeOffset.Parse(
                timestampText,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal).ToOffset(BeijingOffset);
            if (timestamp < startLocal || timestamp >= endLocal)
            {
                return null;
            }

            if (!root.TryGetProperty("payload", out var payload) ||
                !StringEquals(payload, "type", "token_count") ||
                !payload.TryGetProperty("rate_limits", out var rateLimits) ||
                rateLimits.ValueKind is JsonValueKind.Null)
            {
                return null;
            }

            var primary = TryReadRateLimitWindow(rateLimits, "primary");
            var secondary = TryReadRateLimitWindow(rateLimits, "secondary");
            if (primary is null && secondary is null)
            {
                return null;
            }

            var windows = ClassifyRateLimitWindows(primary, secondary);

            var modelContextWindow = payload.TryGetProperty("info", out var info)
                ? GetInt64(info, "model_context_window")
                : 0;
            var limitId = GetString(rateLimits, "limit_id");
            var limitName = GetString(rateLimits, "limit_name");
            NormalizeRateLimitIdentity(modelContextWindow, ref limitId, ref limitName);

            return new RateLimitSnapshot(
                timestamp,
                limitId,
                limitName,
                windows.FiveHour,
                windows.Week,
                modelContextWindow);
        }
        catch
        {
            return null;
        }
    }

    private static RateLimitWindowSnapshot? TryReadRateLimitWindow(JsonElement rateLimits, string propertyName)
    {
        if (!rateLimits.TryGetProperty(propertyName, out var window) ||
            window.ValueKind is JsonValueKind.Null)
        {
            return null;
        }

        var usedPercent = GetDecimal(window, "used_percent");
        var windowMinutes = (int)GetInt64(window, "window_minutes");
        if (!QuotaPercentRules.IsValid(usedPercent) || windowMinutes <= 0)
        {
            return null;
        }

        DateTimeOffset? resetAt = null;
        var resetSeconds = GetInt64(window, "resets_at");
        if (resetSeconds > 0)
        {
            resetAt = DateTimeOffset.FromUnixTimeSeconds(resetSeconds).ToOffset(BeijingOffset);
        }

        return new RateLimitWindowSnapshot(usedPercent!.Value, windowMinutes, resetAt);
    }

    private static (RateLimitWindowSnapshot? FiveHour, RateLimitWindowSnapshot? Week) ClassifyRateLimitWindows(
        RateLimitWindowSnapshot? first,
        RateLimitWindowSnapshot? second)
    {
        var windows = new[] { first, second }
            .Where(window => window is not null)
            .Select(window => window!.Value)
            .GroupBy(window => window.WindowMinutes)
            .Select(group => group.First())
            .ToList();

        // Field position is not stable, but window duration is. Other windows
        // (notably the 30-day reset-card window) are not 5h/7d quota data.
        return (
            windows
                .Where(window => IsFiveHourWindow(window.WindowMinutes))
                .Select(window => (RateLimitWindowSnapshot?)window)
                .FirstOrDefault(),
            windows
                .Where(window => IsWeeklyWindow(window.WindowMinutes))
                .Select(window => (RateLimitWindowSnapshot?)window)
                .FirstOrDefault());
    }

    internal static bool IsFiveHourWindow(int windowMinutes) => windowMinutes == FiveHourWindowMinutes;

    internal static bool IsWeeklyWindow(int windowMinutes) => windowMinutes == WeeklyWindowMinutes;

    private static bool ReadFile(
        string file,
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        TokenUsageSummary summary,
        Dictionary<DateOnly, TokenUsageBucket> dailyBuckets,
        CancellationToken cancellationToken)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var replayFilter = new SubagentReplayFilter();
            while (reader.ReadLine() is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!replayFilter.ShouldReadTokenCount(line))
                {
                    continue;
                }

                ReadLine(line, startLocal, endLocal, summary, dailyBuckets, replayFilter.ModelId, replayFilter.ServiceTier);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }

        return true;
    }

    private static void ReadLine(
        string line,
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        TokenUsageSummary summary,
        Dictionary<DateOnly, TokenUsageBucket> dailyBuckets,
        string? modelId, string? serviceTier)
    {
        var usageEvent = TryReadUsageEvent(line, startLocal, endLocal, modelId, serviceTier);
        if (usageEvent is null)
        {
            return;
        }

        summary.Add(usageEvent);

        var dayKey = DateOnly.FromDateTime(usageEvent.Timestamp.DateTime);
        if (!dailyBuckets.TryGetValue(dayKey, out var bucket))
        {
            bucket = new TokenUsageBucket
            {
                StartLocal = new DateTimeOffset(dayKey.Year, dayKey.Month, dayKey.Day, 0, 0, 0, BeijingOffset)
            };
            dailyBuckets[dayKey] = bucket;
        }

        bucket.Add(usageEvent);
    }

    private static TokenUsageEvent? TryReadUsageEvent(
        string line,
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        string? modelId, string? serviceTier)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (!StringEquals(root, "type", "event_msg") ||
                !root.TryGetProperty("timestamp", out var timestampElement))
            {
                return null;
            }

            var timestampText = timestampElement.GetString();
            if (string.IsNullOrWhiteSpace(timestampText))
            {
                return null;
            }

            var timestamp = DateTimeOffset.Parse(
                timestampText,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal).ToOffset(BeijingOffset);

            if (timestamp < startLocal || timestamp >= endLocal)
            {
                return null;
            }

            if (!root.TryGetProperty("payload", out var payload) ||
                !StringEquals(payload, "type", "token_count") ||
                !payload.TryGetProperty("info", out var info) ||
                info.ValueKind is JsonValueKind.Null ||
                !info.TryGetProperty("last_token_usage", out var usage) ||
                usage.ValueKind is JsonValueKind.Null)
            {
                return null;
            }

            var input = GetInt64(usage, "input_tokens");
            var cached = GetInt64(usage, "cached_input_tokens");
            var cacheWrite = GetInt64(usage, "cache_write_input_tokens");
            var output = GetInt64(usage, "output_tokens");
            var reasoning = GetInt64(usage, "reasoning_output_tokens");
            var total = GetInt64(usage, "total_tokens");
            if (total == 0)
            {
                total = TokenCountMath.AddNonNegative(input, output);
            }

            var key = payload.TryGetProperty("turn_id", out var turnIdElement)
                ? turnIdElement.GetString()
                : null;
            return new TokenUsageEvent(
                timestamp,
                input,
                cached,
                output,
                reasoning,
                total,
                string.IsNullOrWhiteSpace(key) ? null : $"codex:{key}",
                cacheWrite,
                ReadModelId(usage) ?? ReadModelId(info) ?? ReadModelId(payload) ?? modelId,
                ReadServiceTier(usage) ?? ReadServiceTier(info) ?? ReadServiceTier(payload) ?? serviceTier);
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadServiceTier(JsonElement element) =>
        element.TryGetProperty("service_tier", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim().ToLowerInvariant() : null;

    private static string? ReadModelId(JsonElement element)
    {
        foreach (var name in new[] { "model", "model_name" })
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(value.GetString())) return value.GetString();
        return null;
    }

    private static bool StringEquals(JsonElement element, string propertyName, string expected)
    {
        return element.TryGetProperty(propertyName, out var value) &&
               value.ValueKind is JsonValueKind.String &&
               string.Equals(value.GetString(), expected, StringComparison.Ordinal);
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return value.ValueKind is JsonValueKind.String
            ? value.GetString()
            : value.ToString();
    }

    private static long GetInt64(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return 0;
        }

        return value.ValueKind is JsonValueKind.Number && value.TryGetInt64(out var result)
            ? result
            : 0;
    }

    private static decimal? GetDecimal(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind is JsonValueKind.Number && value.TryGetDecimal(out var result))
        {
            return result;
        }

        if (value.ValueKind is JsonValueKind.String &&
            decimal.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out result))
        {
            return result;
        }

        return null;
    }
}
