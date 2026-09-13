namespace CodexTokenMonitor;

internal enum RangeMode
{
    Day,
    Week,
    Month,
    Cycle
}
internal sealed record SelectedRange(
    DateTimeOffset Start,
    DateTimeOffset End,
    string Title,
    string BreakdownTitle,
    RangeMode Mode,
    bool IsCustomStart = false,
    bool FollowsCurrent = false);

internal sealed record UsageQueryResult(
    TokenUsageSummary Summary,
    IReadOnlyList<TokenUsageBucket> BreakdownRows,
    TimeSpan CodingTime,
    CodexQuotaEstimate? Quota,
    IReadOnlyList<CodexQuotaSnapshot> QuotaSnapshots)
{
    public IReadOnlyList<TokenUsageBucket> DetailRows { get; init; } = Array.Empty<TokenUsageBucket>();
    public IReadOnlyList<CacheWarning> CacheWarnings { get; init; } = Array.Empty<CacheWarning>();
}
