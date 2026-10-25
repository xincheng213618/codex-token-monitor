// Kimi native message/timing rules adapted from zoefix/agent-speed (MIT).
// Protocol 1.4 usage.record is retained as a fallback and shares each request's identity.
namespace CodexTokenMonitor;

internal sealed class KimiSpeedParser : IAgentSpeedParser
{
    private readonly string id, session, localAgent;
    private DateTimeOffset? pending, last;
    private string model = "未知模型";
    private string? requestKey;
    private AgentSpeedPhase phase;
    private bool nativeUsage;
    private readonly Dictionary<string, AgentSpeedResponse> responses = new(StringComparer.Ordinal);
    public KimiSpeedParser(string id)
    {
        this.id = id; var agent = new DirectoryInfo(Path.GetDirectoryName(id)!);
        localAgent = agent.Name; session = (agent.Parent?.Parent?.Name ?? id) + ":" + localAgent;
    }
    public AgentSpeedAgent Agent => new(id, session, model, localAgent != "main", phase, last, pending, UsageSource.Kimi, AgentSpeedJson.KimiMode(model),
        ParentSessionId: localAgent != "main" ? session[..^(localAgent.Length)] + "main" : null);
    public void Reset() { pending = last = null; requestKey = null; model = "未知模型"; phase = AgentSpeedPhase.Idle; nativeUsage = false; }
    public IReadOnlyList<AgentSpeedResponse> ReadResponses(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line); var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return [];
            model = AgentSpeedJson.Text(root, "modelAlias") ?? AgentSpeedJson.Text(root, "model") ?? model;
            if ((AgentSpeedJson.Milliseconds(root) ?? AgentSpeedJson.Timestamp(root)) is not { } timestamp) return [];
            last = last > timestamp ? last : timestamp;
            var type = AgentSpeedJson.Text(root, "type");
            var ev = type == "context.append_loop_event" ? AgentSpeedJson.Object(root, "event") : root;
            if (type == "context.append_loop_event") type = AgentSpeedJson.Text(ev, "type");
            if (type == "llm.request")
            {
                pending = timestamp; phase = AgentSpeedPhase.Generating;
                requestKey = AgentSpeedJson.Text(root, "responseId") ?? $"{session}:{AgentSpeedJson.Text(root, "turnStep")}:{timestamp.ToUnixTimeMilliseconds()}";
                return [];
            }
            if (type is "turn.cancel" or "turn.ended") { pending = null; phase = AgentSpeedPhase.Idle; return []; }
            if (type is "tool.call" or "tool.result") { phase = type == "tool.call" ? AgentSpeedPhase.Tools : AgentSpeedPhase.Generating; pending = null; return []; }
            if (type is "step.begin" or "turn.step.started" or "turn.started" or "turn.prompt")
            { pending ??= timestamp; phase = AgentSpeedPhase.Generating; return []; }
            var message = AgentSpeedJson.Object(root, "message"); var meta = AgentSpeedJson.Object(message, "meta");
            if (AgentSpeedJson.Object(message, "message").ValueKind == JsonValueKind.Object) message = AgentSpeedJson.Object(message, "message");
            var isMessage = type is "context.append_message" or "agent.message.appended" or "human.agent.message.appended";
            var role = AgentSpeedJson.Text(message, "role");
            if (isMessage && role != "assistant")
            { if (role == "user") { pending = timestamp; phase = AgentSpeedPhase.Generating; } return []; }
            if (AgentSpeedJson.Flag(message, "partial")) { phase = AgentSpeedPhase.Generating; return []; }
            var usage = type == "usage.record" ? AgentSpeedJson.Object(root, "usage") :
                type == "step.end" ? AgentSpeedJson.Object(ev, "usage") : AgentSpeedJson.Object(message, "usage");
            if (usage.ValueKind != JsonValueKind.Object && isMessage) usage = AgentSpeedJson.Object(meta, "usage");
            if (usage.ValueKind != JsonValueKind.Object || !(isMessage || type is "step.end" or "usage.record")) return [];
            if (type == "usage.record" && (nativeUsage || AgentSpeedJson.Text(root, "usageScope") != "turn")) return [];
            if (type != "usage.record") nativeUsage = true;
            model = AgentSpeedJson.Text(AgentSpeedJson.Object(meta, "model"), "model") ?? AgentSpeedJson.Text(message, "model") ?? model;
            var messageId = AgentSpeedJson.Text(message, "providerMessageId") ?? AgentSpeedJson.Text(message, "id") ?? AgentSpeedJson.Text(meta, "messageId") ?? AgentSpeedJson.Text(ev, "messageId");
            var key = requestKey ?? messageId ?? $"{session}:{timestamp.ToUnixTimeMilliseconds()}";
            var timing = AgentSpeedJson.Object(message, "llmTiming");
            if (timing.ValueKind != JsonValueKind.Object) timing = AgentSpeedJson.Object(meta, "llmTiming");
            if (type == "step.end") timing = ev;
            var start = pending;
            if (start is null && timing.ValueKind == JsonValueKind.Object && timing.TryGetProperty("llmFirstTokenLatencyMs", out _) && timing.TryGetProperty("llmStreamDurationMs", out _))
                start = timestamp.AddMilliseconds(-Math.Min(900_001, AgentSpeedJson.Number(timing, "llmFirstTokenLatencyMs") + AgentSpeedJson.Number(timing, "llmStreamDurationMs")));
            var input = AgentSpeedJson.Number(usage, "inputOther") + AgentSpeedJson.Number(usage, "inputCacheRead") + AgentSpeedJson.Number(usage, "inputCacheCreation");
            var output = AgentSpeedJson.Number(usage, "output");
            var response = new AgentSpeedResponse("kimi:" + key, id, model, start, timestamp, output, input, UsageSource.Kimi,
                ExecutionMode: AgentSpeedJson.KimiMode(model), NativeResponseId: messageId);
            if (responses.TryGetValue(key, out var old)) response = response with { Start = old.Start ?? start, End = old.End > timestamp ? old.End : timestamp,
                OutputTokens = Math.Max(old.OutputTokens, output), InputTokens = Math.Max(old.InputTokens, input) };
            responses[key] = response;
            requestKey = key; pending = null;
            var tools = AgentSpeedJson.Text(ev, "finishReason") is "tool_use" or "tool_calls" ||
                message.ValueKind == JsonValueKind.Object && message.TryGetProperty("toolCalls", out var calls) && calls.ValueKind == JsonValueKind.Array && calls.GetArrayLength() > 0;
            phase = tools ? AgentSpeedPhase.Tools : AgentSpeedPhase.Idle;
            return [response];
        }
        catch (JsonException) { }
        catch (InvalidOperationException) { }
        catch (ArgumentOutOfRangeException) { }
        return [];
    }
}
