// Timing/state rules adapted from zoefix/agent-speed, a94a67f (MIT).
// See docs/third-party/agent-speed for attribution and measurement differences.
namespace CodexTokenMonitor;

/// <summary>Per-file state; content is inspected transiently and is never retained.</summary>
internal sealed class CodexSpeedParser : IAgentSpeedParser
{
    private readonly string agentId;
    private readonly Func<string, bool>? canUse;
    private DateTimeOffset? created, pending, lastActivity;
    private string sessionId, model = "未知模型", agentPath = "/root";
    private string? mode, parentSession;
    private bool streaming, modern, isSubagent;
    private AgentSpeedPhase phase;
    private Counts? previous;
    private readonly HashSet<string> seen = new(StringComparer.Ordinal);
    private SubagentReplayFilter replay = new();

    public CodexSpeedParser(string agentId, Func<string, bool>? canUse = null) { this.agentId = agentId; sessionId = agentId; this.canUse = canUse; }

    public AgentSpeedAgent Agent => new(agentId, sessionId, model, isSubagent, phase, lastActivity, pending,
        UsageSource.Codex, mode, parentSession);
    public IReadOnlyList<AgentSpeedResponse> ReadResponses(string line) => Read(line) is { } response ? [response] : [];

    public void Reset()
    {
        created = pending = lastActivity = null;
        sessionId = agentId; model = "未知模型"; agentPath = "/root";
        mode = parentSession = null;
        streaming = modern = isSubagent = false;
        phase = AgentSpeedPhase.Idle; previous = null;
        // Keep response keys across file truncation/replacement.
        replay = new();
    }

    public AgentSpeedResponse? Read(string line)
    {
        if (line.Length > 32 * 1024 * 1024) return null;
        var legacyAllowed = replay.ShouldReadTokenCount(line);
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !DateTimeOffset.TryParse(Text(root, "timestamp"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var timestamp) ||
                !root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object) return null;
            var kind = Text(root, "type");
            if (kind == "session_meta")
            {
                if (created is not null) return null;
                created = timestamp;
                sessionId = Text(payload, "id") ?? sessionId;
                model = Text(payload, "model") ?? replay.ModelId ?? model;
                agentPath = Text(payload, "agent_path") ?? agentPath;
                isSubagent = Text(payload, "thread_source") == "subagent" ||
                    payload.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.Object &&
                    source.TryGetProperty("subagent", out _);
                parentSession = Text(payload, "parent_thread_id");
                if (parentSession is null && payload.TryGetProperty("source", out var parentSource) &&
                    parentSource.ValueKind == JsonValueKind.Object && parentSource.TryGetProperty("subagent", out var subagent) &&
                    subagent.ValueKind == JsonValueKind.Object && subagent.TryGetProperty("thread_spawn", out var spawn))
                    parentSession = Text(spawn, "parent_thread_id");
                mode = AgentSpeedJson.ModeWithEvidence(replay.ServiceTier, "设置");
                lastActivity = timestamp;
                return null;
            }
            if (created is { } first && timestamp < first.AddSeconds(-2)) return null;
            model = replay.ModelId ?? model;
            mode = AgentSpeedJson.ModeWithEvidence(replay.ServiceTier, "设置");
            if (lastActivity is null || timestamp > lastActivity) lastActivity = timestamp;
            var type = Text(payload, "type");
            if (kind == "turn_context") model = Text(payload, "model") ?? model;
            if (kind == "response_item")
            {
                var role = Text(payload, "role");
                if (type == "reasoning" || type?.EndsWith("_call", StringComparison.Ordinal) == true || type == "message" && role == "assistant")
                {
                    streaming = true;
                    phase = AgentSpeedPhase.Generating;
                    // No inferred start is installed here: output cannot prove when the request began.
                }
                else if (type?.EndsWith("_output", StringComparison.Ordinal) == true || type == "message" && role is "user" or "developer" or "system")
                    Input(timestamp);
                else if (type == "agent_message" && Text(payload, "recipient") == agentPath && !streaming)
                    pending = timestamp; // A mailbox message alone does not prove generation.
            }
            if (kind == "event_msg")
            {
                if (type is "task_started" or "user_message") Input(timestamp);
                if (type is "task_complete" or "turn_aborted")
                {
                    pending = null; streaming = false; phase = AgentSpeedPhase.Idle;
                }
                if (type != "token_count") return null;
                if (modern)
                {
                    if (streaming) AfterUsage(timestamp);
                    return null;
                }
                // Reuse the monitor's protection against timestamp-rewritten child replays.
                // It recognizes compact production JSONL; spaced JSON is covered by creation-time filtering.
                if (isSubagent && line.Contains("\"type\":\"token_count\"", StringComparison.Ordinal) && !legacyAllowed) return null;
                if (!payload.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object) return null;
                var hasTotal = info.TryGetProperty("total_token_usage", out var totalJson) && totalJson.ValueKind == JsonValueKind.Object;
                var hasLast = info.TryGetProperty("last_token_usage", out var lastJson) && lastJson.ValueKind == JsonValueKind.Object;
                if (!hasTotal && !hasLast) return null;
                var total = hasTotal ? Counts.Read(totalJson) : default;
                var last = hasLast ? Counts.Read(lastJson) : total;
                var tokens = last;
                if (hasTotal && previous is { } old)
                {
                    if (total.Total == old.Total) return null;
                    var delta = new Counts(total.Input - old.Input, total.Output - old.Output, total.Total - old.Total);
                    tokens = delta.Input < 0 || delta.Output < 0 || delta.Total < 0 ? last : delta;
                }
                if (hasTotal) previous = total;
                var key = hasTotal ? $"codex-legacy:{sessionId}:{total.Total}"
                    : $"codex-legacy:{sessionId}:{timestamp:O}:{Text(payload, "turn_id")}";
                return Emit(key, tokens, timestamp);
            }
            if (kind == "token_usage_record")
            {
                modern = true;
                if (!payload.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return null;
                model = Text(payload, "model") ?? model;
                var key = Text(payload, "response_id") ?? $"codex:{Text(payload, "thread_id") ?? sessionId}:{timestamp:O}";
                return Emit(key, Counts.Read(usage), timestamp);
            }
        }
        catch (JsonException) { }
        catch (InvalidOperationException) { }
        return null;
    }

    private void Input(DateTimeOffset timestamp)
    {
        if (streaming) return;
        pending = timestamp; phase = AgentSpeedPhase.Generating;
    }

    private AgentSpeedResponse? Emit(string key, Counts tokens, DateTimeOffset timestamp)
    {
        if (canUse?.Invoke(key) == false || !seen.Add(key)) return null;
        var result = new AgentSpeedResponse(key, agentId, model, pending, timestamp, tokens.Output, tokens.Input,
            ExecutionMode: mode);
        AfterUsage(timestamp);
        return result;
    }

    private void AfterUsage(DateTimeOffset timestamp)
    {
        pending = timestamp; streaming = false; phase = AgentSpeedPhase.Tools;
    }

    internal static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private readonly record struct Counts(long Input, long Output, long Total)
    {
        public static Counts Read(JsonElement value)
        {
            var input = Number(value, "input_tokens");
            var output = Number(value, "output_tokens");
            return new(input, output, Math.Max(input + output, Number(value, "total_tokens")));
        }

        private static long Number(JsonElement element, string property) =>
            element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
                ? (long)Math.Clamp(double.IsNaN(number) ? 0 : number, 0, 1_000_000_000_000d) : 0;
    }
}
