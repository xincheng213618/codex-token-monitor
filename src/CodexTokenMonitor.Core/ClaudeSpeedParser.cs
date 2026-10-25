// Claude state and message-upsert rules adapted from zoefix/agent-speed (MIT).
namespace CodexTokenMonitor;

internal sealed class ClaudeSpeedParser : IAgentSpeedParser
{
    private readonly string id;
    private readonly Func<string, bool>? canUse;
    private readonly Dictionary<string, AgentSpeedResponse> responses = new(StringComparer.Ordinal);
    private DateTimeOffset? pending, last;
    private bool precise, subagent;
    private string session, model = "未知模型";
    private string? mode, parent;
    private AgentSpeedPhase phase;

    public ClaudeSpeedParser(string id, Func<string, bool>? canUse = null)
    {
        this.id = id; this.canUse = canUse; session = Path.GetFileNameWithoutExtension(id);
        var directory = Path.GetDirectoryName(id);
        subagent = string.Equals(Path.GetFileName(directory), "subagents", StringComparison.OrdinalIgnoreCase);
        if (subagent) parent = Path.GetFileName(Path.GetDirectoryName(directory));
    }
    public AgentSpeedAgent Agent => new(id, session, model, subagent, phase, last, pending, UsageSource.ClaudeCode, mode, parent);
    public void Reset() { pending = last = null; precise = false; phase = AgentSpeedPhase.Idle; model = "未知模型"; mode = null; }

    public IReadOnlyList<AgentSpeedResponse> ReadResponses(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line); var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || AgentSpeedJson.Timestamp(root) is not { } timestamp) return [];
            last = last > timestamp ? last : timestamp;
            session = AgentSpeedJson.Text(root, "sessionId") ?? session;
            subagent |= AgentSpeedJson.Flag(root, "isSidechain");
            var type = AgentSpeedJson.Text(root, "type");
            if (type == "api-request") { pending = timestamp; precise = true; phase = AgentSpeedPhase.Generating; mode = null; }
            else if (type == "user")
            {
                var message = AgentSpeedJson.Object(root, "message");
                var interrupted = message.TryGetProperty("content", out var content) &&
                    (content.ValueKind == JsonValueKind.String ? content.GetString()?.StartsWith("[Request interrupted by user", StringComparison.Ordinal) == true :
                    content.ValueKind == JsonValueKind.Array && content.EnumerateArray().Any(block => AgentSpeedJson.Text(block, "text")?.StartsWith("[Request interrupted by user", StringComparison.Ordinal) == true));
                if (interrupted) { pending = null; precise = false; phase = AgentSpeedPhase.Idle; }
                else { if (!precise) pending = timestamp; phase = AgentSpeedPhase.Generating; mode = null; }
            }
            else if (type == "assistant")
            {
                var message = AgentSpeedJson.Object(root, "message");
                var key = AgentSpeedJson.Text(message, "id") ?? AgentSpeedJson.Text(root, "requestId") ?? AgentSpeedJson.Text(root, "uuid");
                var nextModel = AgentSpeedJson.Text(message, "model");
                if (key is null || nextModel == "<synthetic>") return [];
                if (canUse?.Invoke("claude:" + key) == false) return [];
                var usage = AgentSpeedJson.Object(message, "usage");
                var stop = AgentSpeedJson.Text(message, "stop_reason");
                var final = message.ValueKind == JsonValueKind.Object && message.TryGetProperty("stop_reason", out var reason) && reason.ValueKind != JsonValueKind.Null;
                var input = AgentSpeedJson.Number(usage, "input_tokens") + AgentSpeedJson.Number(usage, "cache_read_input_tokens") + AgentSpeedJson.Number(usage, "cache_creation_input_tokens");
                var output = AgentSpeedJson.Number(usage, "output_tokens");
                var nextMode = AgentSpeedJson.ModeWithEvidence(AgentSpeedJson.Text(usage, "speed"), "回执");
                var response = new AgentSpeedResponse("claude:" + key, id, nextModel ?? model, pending, timestamp, output, input,
                    UsageSource.ClaudeCode, final, nextMode);
                if (responses.TryGetValue(key, out var old)) response = response with
                {
                    Start = old.Start, End = timestamp > old.End ? timestamp : old.End,
                    OutputTokens = Math.Max(old.OutputTokens, output), InputTokens = Math.Max(old.InputTokens, input),
                    Final = old.Final || final, ExecutionMode = old.ExecutionMode == "Fast（回执）" ? old.ExecutionMode : nextMode ?? old.ExecutionMode
                };
                else { pending = null; precise = false; }
                responses[key] = response; model = response.Model; mode = response.ExecutionMode;
                phase = stop switch { "tool_use" => AgentSpeedPhase.Tools, "pause_turn" => AgentSpeedPhase.Generating,
                    null => AgentSpeedJson.HasBlock(message, "tool_use") ? AgentSpeedPhase.Tools : AgentSpeedPhase.Generating, _ => AgentSpeedPhase.Idle };
                pending = phase == AgentSpeedPhase.Generating ? response.Start ?? timestamp : null;
                return [response];
            }
        }
        catch (JsonException) { }
        catch (InvalidOperationException) { }
        return [];
    }
}
