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
    private UsageCounters? previousTotal;
    private UsageCounters? previousLast;
    private string? usageSessionId;
    public bool IsRepeatedUsage { get; private set; }

    // Quota-only updates repeat the last request's usage with an unchanged
    // cumulative counter. Keep those updates for quota scanning, but not tokens.
    public bool ShouldReadUsage(string line)
    {
        IsRepeatedUsage = false;
        var accepted = ShouldReadTokenCount(line);
        if (!accepted && !line.Contains("\"session_meta\"", StringComparison.Ordinal)) return false;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("payload", out var payload) ||
                payload.ValueKind != JsonValueKind.Object) return accepted;
            if (!accepted)
            {
                if (payload.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                {
                    var next = id.GetString();
                    if (usageSessionId is not null && next != usageSessionId)
                        (previousTotal, previousLast) = (null, null);
                    usageSessionId = next;
                }
                return false;
            }
            if (!payload.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object)
                return true;
            var total = UsageCounters.Read(info, "total_token_usage");
            var last = UsageCounters.Read(info, "last_token_usage");
            IsRepeatedUsage = total is not null && last is not null && total == previousTotal &&
                (last == previousLast || last.Value.Input == 0 && last.Value.Output == 0);
            if (total is not null)
                (previousTotal, previousLast) = (total, last);
            return !IsRepeatedUsage;
        }
        catch (JsonException) { return accepted; }
    }

    private readonly record struct UsageCounters(long Input, long Cached, long CacheWrite,
        long Output, long Reasoning, long Total)
    {
        public static UsageCounters? Read(JsonElement info, string name)
        {
            if (!info.TryGetProperty(name, out var usage) || usage.ValueKind != JsonValueKind.Object ||
                !usage.TryGetProperty("total_tokens", out var total) || total.ValueKind != JsonValueKind.Number ||
                !total.TryGetInt64(out _)) return null;
            long Get(string key) => usage.TryGetProperty(key, out var v) &&
                v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;
            return new(Get("input_tokens"), Get("cached_input_tokens"), Get("cache_write_input_tokens"),
                Get("output_tokens"), Get("reasoning_output_tokens"), Get("total_tokens"));
        }
    }

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
