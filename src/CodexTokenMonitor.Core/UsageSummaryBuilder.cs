namespace CodexTokenMonitor;

internal static class UsageSummaryBuilder
{
    public static TokenUsageSummary FromRows(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        IEnumerable<TokenUsageBucket> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        startLocal = startLocal.ToOffset(CodexUsageReader.BeijingOffset);
        endLocal = endLocal.ToOffset(CodexUsageReader.BeijingOffset);

        var summary = new TokenUsageSummary
        {
            StartLocal = startLocal,
            EndLocal = endLocal
        };
        foreach (var row in rows)
        {
            summary.MergeFrom(row);
        }

        return summary;
    }
}
