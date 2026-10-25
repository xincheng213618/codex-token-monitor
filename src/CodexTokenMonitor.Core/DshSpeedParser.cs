// DSH state/normalization adapted from zoefix/agent-speed (MIT), with legacy settlements retained.
namespace CodexTokenMonitor;

internal sealed class DshSpeedParser(string id, Func<string, bool>? canUse = null) : IAgentSpeedParser
{
    private string session = Path.GetFileName(Path.GetDirectoryName(id)) ?? id, model = "未知模型";
    private string? parent, mode;
    private DateTimeOffset? created, pending, last;
    private bool child, metadataSeen;
    private readonly HashSet<string> seen = new(StringComparer.Ordinal);
    private AgentSpeedPhase phase;
    public AgentSpeedAgent Agent => new(id, session, model, child, phase, last, pending, UsageSource.Dsh, mode, parent);
    public void Reset() { created = pending = last = null; parent = mode = null; model = "未知模型"; child = metadataSeen = false; phase = AgentSpeedPhase.Idle; }
    public IReadOnlyList<AgentSpeedResponse> ReadResponses(string line)
    {
        var result = new List<AgentSpeedResponse>();
        try { using var document = JsonDocument.Parse(line); Apply(document.RootElement, result); }
        catch (JsonException) { }
        catch (InvalidOperationException) { }
        return result;
    }
    private void Apply(JsonElement root, List<AgentSpeedResponse> result)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        var type = AgentSpeedJson.Text(root, "type");
        if (type == "event") { Apply(AgentSpeedJson.Object(root, "event"), result); return; }
        if (type == "snapshot")
        {
            Apply(AgentSpeedJson.Object(root, "header"), result);
            if (root.TryGetProperty("records", out var records) && records.ValueKind == JsonValueKind.Array)
                foreach (var item in records.EnumerateArray()) Apply(item, result);
            return;
        }
        if (type == "session")
        {
            if (metadataSeen) return;
            metadataSeen = true;
            created = AgentSpeedJson.Milliseconds(root, "createdAt") ?? AgentSpeedJson.Milliseconds(root) ?? AgentSpeedJson.Timestamp(root, "createdAt");
            session = AgentSpeedJson.Text(root, "id") ?? session;
            parent = AgentSpeedJson.Text(root, "parentSession");
            child = parent is not null || AgentSpeedJson.Text(root, "origin") == "subagent" || AgentSpeedJson.Number(root, "delegationDepth") > 0;
            return;
        }
        if (AgentSpeedJson.Milliseconds(root) is not { } time || created is { } first && time < first.AddSeconds(-2)) return;
        last = last > time ? last : time;
        var data = AgentSpeedJson.Object(root, "data");
        if (type is "request/header" or "request/context")
        {
            var config = type == "request/context" ? data : AgentSpeedJson.Object(AgentSpeedJson.Object(data, "header"), "config");
            model = AgentSpeedJson.Text(config, "model") ?? model;
            mode = AgentSpeedJson.ModeWithEvidence(AgentSpeedJson.Text(config, "speed") ?? AgentSpeedJson.Text(config, "serviceTier"), "设置");
        }
        else if (type == "step/start") { pending = time; phase = AgentSpeedPhase.Generating; }
        else if (type is "turn/end" or "turn/cancel") { pending = null; phase = AgentSpeedPhase.Idle; }
        else if (type is "assistant/message" or "assistant/chunk")
        {
            var message = AgentSpeedJson.Object(data, "message");
            var usage = type == "assistant/message" ? AgentSpeedJson.Object(data, "usage") :
                AgentSpeedJson.Text(AgentSpeedJson.Object(data, "chunk"), "type") == "usage" ? AgentSpeedJson.Object(AgentSpeedJson.Object(data, "chunk"), "usage") : default;
            var key = AgentSpeedJson.Text(message, "id") ?? $"{session}:{AgentSpeedJson.Number(root, "seq")}";
            if ((type == "assistant/message" || usage.ValueKind == JsonValueKind.Object) && (canUse?.Invoke("dsh:" + key) == false || !seen.Add(key))) return;
            if (usage.ValueKind == JsonValueKind.Object)
            {
                model = AgentSpeedJson.Text(AgentSpeedJson.Object(message, "source"), "model") ?? model;
                result.Add(new("dsh:" + key, id, model, pending, time, AgentSpeedJson.Number(usage, "outputTokens"),
                    AgentSpeedJson.Number(usage, "inputTokens") + AgentSpeedJson.Number(usage, "cacheReadTokens") + AgentSpeedJson.Number(usage, "cacheWriteTokens"),
                    UsageSource.Dsh, ExecutionMode: mode));
            }
            if (type == "assistant/message" || usage.ValueKind == JsonValueKind.Object)
            {
                pending = null;
                phase = AgentSpeedJson.HasBlock(message, "tool-call") ? AgentSpeedPhase.Tools : AgentSpeedPhase.Idle;
            }
        }
    }
}
