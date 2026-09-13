namespace CodexTokenMonitor;

internal sealed record UsageQueryRequest(
    IUsageQuery Reader,
    SelectedRange Range,
    bool CacheOnly,
    bool IncludeLiveToday,
    CodexQuotaEstimate? CachedQuota);

internal sealed record UsageCacheQueryRequest(
    IUsageCacheQuery Reader,
    SelectedRange Range,
    CodexQuotaEstimate? CachedQuota);

internal interface IUsageQuotaReader
{
    CodexQuotaEstimate? ReadCachedEstimate(CancellationToken cancellationToken = default);

    IReadOnlyList<CodexQuotaSnapshot> ReadCachedTimeline(
        IReadOnlyList<DateTimeOffset> anchors,
        IReadOnlyList<CodexQuotaSnapshot> supplementalSnapshots,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Coordinates one usage query without window state or thread scheduling.
/// The caller owns serialization with imports, cache invalidation and warmup.
/// CacheOnly forbids source log scans and usage cache repair, including custom
/// ranges. Quota history and timeline data are application-owned caches.
/// </summary>
internal sealed class UsageQueryService
{
    private static readonly TimeSpan MultiDayBreakdownInterval = TimeSpan.FromMinutes(10);
    private readonly IUsageQuotaReader quotaReader;
    private readonly TimeProvider timeProvider;

    public UsageQueryService(IUsageQuotaReader? quotaReader = null, TimeProvider? timeProvider = null)
    {
        this.quotaReader = quotaReader ?? new CodexUsageQuotaReader();
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public UsageQueryResult Execute(UsageQueryRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.CacheOnly)
        {
            return ExecuteCached(new(request.Reader, request.Range, request.CachedQuota), cancellationToken);
        }

        using var diagnostics = CacheOperationDiagnostics.Begin();
        var result = ExecuteCore(request, cancellationToken);
        return result with { CacheWarnings = diagnostics.Warnings };
    }

    /// <summary>
    /// Queries usage through cache capabilities only. The caller must still hold
    /// its I/O gate: store bootstrap and missing quota timeline anchors can write
    /// application caches even though this entry point cannot scan source logs.
    /// </summary>
    public UsageQueryResult ExecuteCached(UsageCacheQueryRequest request, CancellationToken cancellationToken = default)
    {
        using var diagnostics = CacheOperationDiagnostics.Begin();
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Reader);
        ArgumentNullException.ThrowIfNull(request.Range);
        cancellationToken.ThrowIfCancellationRequested();

        var reader = request.Reader;
        var range = request.Range;
        var now = timeProvider.GetUtcNow();
        if (range.Start >= range.End)
        {
            return EmptyResult(range);
        }

        // Custom starts derive their summary from the clipped event rows. Other
        // ranges preserve cached aggregate totals even when details are missing.
        var summary = range.IsCustomStart
            ? null
            : reader.ReadCachedRange(range.Start, range.End, cancellationToken);
        var detailRows = reader.ReadCachedDetailRows(range.Start, range.End, cancellationToken);
        summary ??= UsageSummaryBuilder.FromRows(range.Start, range.End, detailRows);
        var rows = range.IsCustomStart
            ? detailRows
            : UsageBreakdownBuilder.Build(range, summary, detailRows, MultiDayBreakdownInterval);
        var result = CreateResult(reader, range, request.CachedQuota, summary, rows, detailRows,
            UsageBreakdownBuilder.EstimateCodingTime(detailRows), now, cancellationToken);
        return result with { CacheWarnings = diagnostics.Warnings };
    }

    private UsageQueryResult ExecuteCore(UsageQueryRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Reader);
        ArgumentNullException.ThrowIfNull(request.Range);
        cancellationToken.ThrowIfCancellationRequested();

        var reader = request.Reader;
        var range = request.Range;
        var includeLiveToday = request.IncludeLiveToday;
        var now = timeProvider.GetUtcNow();
        if (range.Start >= range.End)
        {
            return EmptyResult(range);
        }

        if (range.IsCustomStart)
        {
            var customRows = reader.ReadTransientDetailRows(range.Start, range.End, cancellationToken);
            return CreateResult(reader, range, request.CachedQuota,
                UsageSummaryBuilder.FromRows(range.Start, range.End, customRows),
                customRows, customRows, UsageBreakdownBuilder.EstimateCodingTime(customRows), now, cancellationToken);
        }

        if (range.Mode == RangeMode.Day)
        {
            var dayUsage = reader.ReadDay(range.Start, range.End, includeLiveToday, cancellationToken);
            var dayRows = dayUsage.Rows;
            var daySummary = dayUsage.Summary;
            if ((!includeLiveToday &&
                  (daySummary.Events > 0 || dayRows.Count > 0) &&
                  HasIncompleteHistoricalCache(reader, range, now, cancellationToken)) ||
                daySummary.Events > 0 && UsageBreakdownBuilder.CountEvents(dayRows) < daySummary.Events)
            {
                dayRows = reader.ReadDetailRows(range.Start, range.End,
                    includeLiveToday: false, cancellationToken: cancellationToken);
                daySummary = UsageSummaryBuilder.FromRows(range.Start, range.End, dayRows);
            }

            return CreateResult(reader, range, request.CachedQuota, daySummary, dayRows, dayRows,
                UsageBreakdownBuilder.EstimateCodingTime(dayRows), now, cancellationToken);
        }

        var summary = includeLiveToday
            ? reader.ReadRange(range.Start, range.End, includeLiveToday, cancellationToken)
            : reader.ReadCachedRange(range.Start, range.End, cancellationToken);
        var detailRows = reader.ReadCachedDetailRows(range.Start, range.End, cancellationToken);
        if (!includeLiveToday &&
            (summary.Events > 0 || detailRows.Count > 0) &&
            HasIncompleteHistoricalCache(reader, range, now, cancellationToken))
        {
            summary = reader.ReadRange(range.Start, range.End,
                includeLiveToday: false, cancellationToken: cancellationToken);
            detailRows = reader.ReadCachedDetailRows(range.Start, range.End, cancellationToken);
        }

        if (summary.Events > 0 &&
            UsageBreakdownBuilder.CountEvents(detailRows) < summary.Events)
        {
            detailRows = UsageBreakdownBuilder.ReadDetailRowsForRange(range.Start, range.End,
                (dayStart, dayEnd) => reader.ReadDetailRows(dayStart, dayEnd, includeLiveToday, cancellationToken));
        }

        var rows = UsageBreakdownBuilder.Build(range, summary, detailRows, MultiDayBreakdownInterval);
        var codingTime = UsageBreakdownBuilder.EstimateCodingTimeForRange(
            reader, range, rows, detailRows, includeLiveToday,
            !includeLiveToday, cancellationToken);
        return CreateResult(reader, range, request.CachedQuota, summary, rows, detailRows, codingTime, now, cancellationToken);
    }

    private static UsageQueryResult EmptyResult(SelectedRange range) => new(
        UsageSummaryBuilder.FromRows(range.Start, range.End, Array.Empty<TokenUsageBucket>()),
        Array.Empty<TokenUsageBucket>(), TimeSpan.Zero, null, Array.Empty<CodexQuotaSnapshot>());

    public bool CanCacheCycleResult(
        IUsageCacheQuery reader,
        SelectedRange range,
        bool includeLiveToday,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (includeLiveToday || range.Mode != RangeMode.Cycle || range.IsCustomStart || range.Start >= range.End)
        {
            return false;
        }

        var todayStart = StartOfDay(timeProvider.GetUtcNow());
        var lastIncluded = range.End.AddTicks(-1);
        return lastIncluded < todayStart &&
               reader.GetIncompleteHistoricalDays(range.Start, lastIncluded, cancellationToken).Count == 0;
    }

    public bool HasIncompleteHistoricalCache(
        IUsageCacheQuery reader,
        SelectedRange range,
        CancellationToken cancellationToken = default)
    {
        return HasIncompleteHistoricalCache(reader, range, timeProvider.GetUtcNow(), cancellationToken);
    }

    private static bool HasIncompleteHistoricalCache(
        IUsageCacheQuery reader,
        SelectedRange range,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var todayStart = StartOfDay(now);
        var historicalEnd = range.End < todayStart ? range.End : todayStart;
        return range.Start < historicalEnd &&
               reader.GetIncompleteHistoricalDays(range.Start, historicalEnd.AddTicks(-1), cancellationToken).Count > 0;
    }

    private UsageQueryResult CreateResult(
        IUsageCacheQuery reader,
        SelectedRange range,
        CodexQuotaEstimate? cachedQuota,
        TokenUsageSummary summary,
        IReadOnlyList<TokenUsageBucket> rows,
        IReadOnlyList<TokenUsageBucket> detailRows,
        TimeSpan codingTime,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CodexQuotaEstimate? quota = null;
        IReadOnlyList<CodexQuotaSnapshot> snapshots = Array.Empty<CodexQuotaSnapshot>();
        if (reader.SupportsQuota)
        {
            quota = cachedQuota is not null && QuotaFreshness.IsFresh(cachedQuota.SnapshotLocal, now)
                ? cachedQuota
                : quotaReader.ReadCachedEstimate(cancellationToken);
            var supplemental = quota is null
                ? Array.Empty<CodexQuotaSnapshot>()
                : new[] { new CodexQuotaSnapshot(quota.SnapshotLocal, quota.LimitId, quota.LimitName,
                    quota.FiveHour?.UsedPercent, quota.FiveHour?.ResetAtLocal,
                    quota.Week?.UsedPercent, quota.Week?.ResetAtLocal) };
            snapshots = quotaReader.ReadCachedTimeline(
                BuildQuotaAnchors(range, rows), supplemental, cancellationToken);
        }

        // A provider can observe cancellation during its final cache read and
        // return normally. Do not publish that late success to either entry.
        cancellationToken.ThrowIfCancellationRequested();
        return new UsageQueryResult(summary, rows, codingTime, quota, snapshots) { DetailRows = detailRows };
    }

    private static IReadOnlyList<DateTimeOffset> BuildQuotaAnchors(
        SelectedRange range,
        IReadOnlyList<TokenUsageBucket> rows)
    {
        return rows.Select(bucket =>
            {
                if (IsEventBucket(range, bucket)) return bucket.StartLocal;
                var bucketEnd = bucket.StartLocal.Add(GetQuotaBucketInterval(range));
                return (bucketEnd < range.End ? bucketEnd : range.End).AddTicks(-1);
            })
            .Where(anchor => anchor >= range.Start && anchor < range.End)
            .Distinct()
            .ToList();
    }

    internal static bool IsEventBucket(SelectedRange range, TokenUsageBucket bucket)
    {
        return range.IsCustomStart || range.Mode == RangeMode.Day ||
            range.Mode is RangeMode.Week or RangeMode.Cycle &&
            (bucket.LastTokenEventLocal is null || bucket.LastTokenEventLocal < bucket.StartLocal.Add(MultiDayBreakdownInterval));
    }

    internal static TimeSpan GetQuotaBucketInterval(SelectedRange range)
    {
        return range.Mode is RangeMode.Week or RangeMode.Cycle
            ? MultiDayBreakdownInterval
            : TimeSpan.FromDays(1);
    }

    private static DateTimeOffset StartOfDay(DateTimeOffset value)
    {
        var local = value.ToOffset(CodexUsageReader.BeijingOffset);
        return new DateTimeOffset(local.Year, local.Month, local.Day, 0, 0, 0, CodexUsageReader.BeijingOffset);
    }
}

internal sealed class CodexUsageQuotaReader : IUsageQuotaReader
{
    public CodexQuotaEstimate? ReadCachedEstimate(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // quota-history-v2.jsonl belongs to this application's persisted cache.
        // This path reads cached usage for the window estimates, never sessions.
        return CodexUsageReader.ReadCachedQuotaEstimate(cancellationToken);
    }

    public IReadOnlyList<CodexQuotaSnapshot> ReadCachedTimeline(
        IReadOnlyList<DateTimeOffset> anchors,
        IReadOnlyList<CodexQuotaSnapshot> supplementalSnapshots,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Missing anchors are derived only from application-owned quota caches.
        return CodexUsageReader.ReadMaterializedQuotaTimeline(
            anchors, supplementalSnapshots, refreshExisting: false, cancellationToken: cancellationToken);
    }
}
