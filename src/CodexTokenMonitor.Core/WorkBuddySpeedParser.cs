// Native item/parallel-tool/upsert rules adapted from zoefix/agent-speed (MIT).
namespace CodexTokenMonitor;

internal sealed class WorkBuddySpeedParser(string id, Func<string, bool>? canUse = null) : IAgentSpeedParser
{
    private readonly Dictionary<string, AgentSpeedResponse> responses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> openCalls = new(StringComparer.Ordinal);
    private string model = "未知模型", session = Path.GetFileNameWithoutExtension(id);
    private string? mode;
    private DateTimeOffset? pending, last;
    private AgentSpeedPhase phase;
    public AgentSpeedAgent Agent
    {
        get
        {
            var directory = Path.GetDirectoryName(id); var child = string.Equals(Path.GetFileName(directory), "subagents", StringComparison.OrdinalIgnoreCase);
            return new(id, session, model, child, phase, last, pending, UsageSource.WorkBuddy, mode,
                child ? Path.GetFileName(Path.GetDirectoryName(directory)) : null);
        }
    }
    public void Reset() { pending = last = null; mode = null; model = "未知模型"; phase = AgentSpeedPhase.Idle; openCalls.Clear(); }
    public IReadOnlyList<AgentSpeedResponse> ReadResponses(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line); var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return [];
            var message = AgentSpeedJson.Object(root, "message");
            var time = AgentSpeedJson.Timestamp(root) ?? AgentSpeedJson.Milliseconds(root, "timestamp") ?? AgentSpeedJson.Timestamp(message) ?? AgentSpeedJson.Milliseconds(message, "timestamp");
            if (time is not { } timestamp) return [];
            last = last > timestamp ? last : timestamp;
            session = AgentSpeedJson.Text(root, "sessionId") ?? session;
            var type = AgentSpeedJson.Text(root, "type");
            var role = AgentSpeedJson.Text(root, "role") ?? AgentSpeedJson.Text(message, "role");
            var provider = AgentSpeedJson.Object(root, "providerData");
            var skip = AgentSpeedJson.Flag(provider, "skipRun");
            if ((type is "message" or "user") && role == "user" || type == "user")
            {
                if (!skip) { pending = timestamp; phase = AgentSpeedPhase.Generating; openCalls.Clear(); mode = null; }
                return [];
            }
            if (type == "function_call_result")
            {
                if (AgentSpeedJson.Text(root, "callId") is { } callId) openCalls.Remove(callId);
                if (openCalls.Count == 0) { phase = skip ? AgentSpeedPhase.Idle : AgentSpeedPhase.Generating; pending = skip ? null : timestamp; }
                return [];
            }
            if (skip)
            {
                if (AgentSpeedJson.Text(root, "status") == "incomplete" || AgentSpeedJson.Object(provider, "error").ValueKind == JsonValueKind.Object)
                { phase = AgentSpeedPhase.Idle; pending = null; openCalls.Clear(); }
                return [];
            }
            if (AgentSpeedJson.Flag(provider, "isPartialAborted") || role is "tool" or "system") return [];
            var legacyUsage = AgentSpeedJson.Object(message, "usage");
            var usage = AgentSpeedJson.Object(provider, "usage"); var raw = AgentSpeedJson.Object(provider, "rawUsage");
            if (type is not ("message" or "assistant" or "function_call" or "reasoning" or "model-usage") && legacyUsage.ValueKind != JsonValueKind.Object) return [];
            var key = AgentSpeedJson.Text(provider, "messageId") ?? AgentSpeedJson.Text(message, "id") ?? AgentSpeedJson.Text(root, "id") ?? AgentSpeedJson.Text(root, "uuid");
            if (key is null) return [];
            if (canUse?.Invoke("workbuddy:" + key) == false) return [];
            var input = Math.Max(AgentSpeedJson.Number(legacyUsage, "input_tokens"), Math.Max(AgentSpeedJson.Number(usage, "inputTokens"), AgentSpeedJson.Number(raw, "prompt_tokens")));
            var output = Math.Max(AgentSpeedJson.Number(legacyUsage, "output_tokens"), Math.Max(AgentSpeedJson.Number(usage, "outputTokens"), AgentSpeedJson.Number(raw, "completion_tokens")));
            var nextModel = Model(provider, message);
            var nextMode = AgentSpeedJson.ModeWithEvidence(AgentSpeedJson.Text(provider, "speed") ?? AgentSpeedJson.Text(provider, "serviceTier"), "记录");
            if (nextMode is null && AgentSpeedJson.Text(provider, "requestModelId") is "fast-model" or "balanced-model" or "deep-model" or "auto")
                nextMode = AgentSpeedJson.Text(provider, "requestModelId") + "（路由设置）";
            AgentSpeedResponse? response = null;
            if (input > 0 || output > 0)
            {
                response = new("workbuddy:" + key, id, nextModel, pending, timestamp, output, input, UsageSource.WorkBuddy, ExecutionMode: nextMode);
                if (responses.TryGetValue(key, out var old)) response = response with { Start = old.Start, Model = nextModel == "未知模型" ? old.Model : nextModel,
                    End = timestamp > old.End ? timestamp : old.End, InputTokens = Math.Max(old.InputTokens, input), OutputTokens = Math.Max(old.OutputTokens, output), ExecutionMode = nextMode ?? old.ExecutionMode };
                else pending = null;
                responses[key] = response; model = response.Model; mode = response.ExecutionMode;
            }
            if (type == "model-usage") { phase = AgentSpeedPhase.Idle; pending = null; openCalls.Clear(); }
            else
            {
                foreach (var call in openCalls.Where(p => p.Value != key).Select(p => p.Key).ToArray()) openCalls.Remove(call);
                if (type == "function_call")
                {
                    if (AgentSpeedJson.Text(root, "callId") is { } callId) openCalls[callId] = key;
                    phase = AgentSpeedPhase.Tools;
                }
                else if ((type is "message" or "assistant" || legacyUsage.ValueKind == JsonValueKind.Object) && openCalls.Count == 0)
                {
                    var compact = AgentSpeedJson.Text(provider, "agent") == "compact";
                    phase = compact ? AgentSpeedPhase.Generating : AgentSpeedPhase.Idle;
                    // Text can precede the tool items carrying this call's usage. Keep its start until settlement.
                    if (compact) pending = timestamp;
                }
            }
            return response is null ? [] : [response];
        }
        catch (JsonException) { }
        catch (InvalidOperationException) { }
        return [];
    }
    private static string Model(JsonElement provider, JsonElement message)
    {
        var selected = new[] { AgentSpeedJson.Text(provider, "model"), AgentSpeedJson.Text(provider, "requestModelId"), AgentSpeedJson.Text(message, "model") }
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
        if (selected?.StartsWith("custom-local:", StringComparison.OrdinalIgnoreCase) == true) selected = selected[13..].Trim();
        else if (selected?.StartsWith("custom:", StringComparison.OrdinalIgnoreCase) == true) selected = selected[7..].Trim();
        return string.IsNullOrWhiteSpace(selected) ? "未知模型" : selected;
    }
}
