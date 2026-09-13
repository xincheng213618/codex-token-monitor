namespace CodexTokenMonitor;

internal sealed record ScanRange(DateTimeOffset StartLocal, DateTimeOffset EndLocal, bool CacheHistoricalDays);

/// <summary>
/// Child rollout files begin with a timestamp-rewritten replay of the parent task.
/// Only token_count records after the child task boundary belong to the child.
/// </summary>
internal sealed class SubagentReplayFilter
{
    private readonly CodexModelContext modelContext = new();
    public string? ModelId => modelContext.ModelId;
    public string? ServiceTier => modelContext.ServiceTier;
    private const string CollaborationBootstrap =
        "You are an agent in a team of agents collaborating to complete a task.";

    private bool metadataChecked;
    private bool isSubagent;
    private bool waitingForLiveTaskStart;
    private bool liveTrafficStarted = true;
    private DateTimeOffset? lastReplayTimestamp;

    public bool ShouldReadTokenCount(string line)
    {
        modelContext.Observe(line);
        var isTokenCount = line.Contains("\"type\":\"token_count\"", StringComparison.Ordinal);

        if (!metadataChecked && line.Contains("\"type\":\"session_meta\"", StringComparison.Ordinal))
        {
            metadataChecked = true;
            isSubagent = line.Contains("\"thread_source\":\"subagent\"", StringComparison.Ordinal) ||
                         (line.Contains("\"forked_from_id\"", StringComparison.Ordinal) &&
                          line.Contains("\"parent_thread_id\"", StringComparison.Ordinal));
            liveTrafficStarted = !isSubagent;
            lastReplayTimestamp = TryReadRecordTimestamp(line);
            return false;
        }

        if (!isSubagent || liveTrafficStarted)
        {
            return isTokenCount;
        }

        if (line.Contains(CollaborationBootstrap, StringComparison.Ordinal))
        {
            waitingForLiveTaskStart = true;
            return false;
        }

        if (waitingForLiveTaskStart &&
            line.Contains("\"type\":\"task_started\"", StringComparison.Ordinal))
        {
            liveTrafficStarted = true;
            return false;
        }

        if (line.Contains("\"type\":\"inter_agent_communication_metadata\"", StringComparison.Ordinal))
        {
            liveTrafficStarted = true;
            return false;
        }

        if (!isTokenCount)
        {
            return false;
        }

        var timestamp = TryReadRecordTimestamp(line);
        var previousTimestamp = lastReplayTimestamp;
        if (timestamp is not null)
        {
            lastReplayTimestamp = timestamp;
        }

        if (timestamp is not null && previousTimestamp is not null &&
            timestamp.Value - previousTimestamp.Value >= TimeSpan.FromSeconds(1))
        {
            liveTrafficStarted = true;
            return true;
        }

        return false;
    }

    internal static DateTimeOffset? TryReadRecordTimestamp(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.TryGetProperty("timestamp", out var timestampElement) &&
                timestampElement.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(
                    timestampElement.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out var timestamp))
            {
                return timestamp;
            }
        }
        catch
        {
            // A malformed record is ignored by the normal JSONL parser as well.
        }

        return null;
    }
}
