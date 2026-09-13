namespace CodexTokenMonitor;

/// <summary>
/// Queries application usage caches without scanning source logs or repairing
/// usage data. Store bootstrap may still initialize/migrate SQLite and clean up
/// legacy derived files; this is not a promise of a read-only file connection.
/// </summary>
internal interface IUsageCacheQuery
{
    UsageSource Source { get; }
    string Title { get; }
    bool SupportsQuota { get; }

    TokenUsageSummary ReadCachedRange(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        CancellationToken cancellationToken = default);
    IReadOnlyList<TokenUsageBucket> ReadCachedDetailRows(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        CancellationToken cancellationToken = default);
    IReadOnlyList<DateTimeOffset> GetIncompleteHistoricalDays(
        DateTimeOffset startInclusive,
        DateTimeOffset endInclusive,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Queries that may scan source logs or repair cached usage, including when
/// includeLiveToday is false. The caller owns coordination with maintenance.
/// </summary>
internal interface IUsageQuery : IUsageCacheQuery
{
    TokenUsageSummary ReadRange(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        bool includeLiveToday,
        CancellationToken cancellationToken = default);
    IReadOnlyList<TokenUsageBucket> ReadDetailRows(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        bool includeLiveToday,
        CancellationToken cancellationToken = default);
    IReadOnlyList<TokenUsageBucket> ReadTransientDetailRows(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        CancellationToken cancellationToken = default);
    DailyUsageSnapshot ReadDay(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        bool includeLiveToday,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Explicit cache invalidation and source-log backfill. Quota materialization
/// remains a separate Codex concern and is not implied by these interfaces.
/// </summary>
internal interface IUsageCacheMaintenance
{
    bool ClearCache();
    bool RefreshCachedDay(DateOnly date, CancellationToken cancellationToken = default);
    void WarmHistoricalDay(DateTimeOffset dayStart, CancellationToken cancellationToken = default);
    void WarmHistoricalDays(
        IEnumerable<DateTimeOffset> daysLocal,
        CancellationToken cancellationToken = default,
        Action<DateTimeOffset>? dayCompleted = null,
        Action<int, int>? fileProgress = null);
}
