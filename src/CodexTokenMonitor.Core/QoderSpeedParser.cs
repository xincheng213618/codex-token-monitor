namespace CodexTokenMonitor;

/// <summary>Pairs Qoder's native model.request.started and model.response.completed records by request id.</summary>
internal sealed class QoderSpeedParser(string id) : IAgentSpeedParser
{
    private readonly Dictionary<string, DateTimeOffset> starts = new(StringComparer.Ordinal);
    private string model = "未知模型";
    private DateTimeOffset? last;
    private AgentSpeedPhase phase;
    private string? current;
    public AgentSpeedAgent Agent => new(id, Path.GetFileName(id), model, false, phase, last,
        current is not null && starts.TryGetValue(current, out var start) ? start : null, UsageSource.Qoder);
    public void Reset() { starts.Clear(); last = null; phase = AgentSpeedPhase.Idle; current = null; model = "未知模型"; }
    public IReadOnlyList<AgentSpeedResponse> ReadResponses(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line); var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || AgentSpeedJson.Timestamp(root, "ts") is not { } time) return [];
            last = last > time ? last : time;
            var type = AgentSpeedJson.Text(root, "type"); var data = AgentSpeedJson.Object(root, "data");
            var request = AgentSpeedJson.Text(root, "request_id");
            if (type == "model.request.started")
            {
                model = AgentSpeedJson.Text(data, "model") ?? model; phase = AgentSpeedPhase.Generating;
                current = request; if (request is not null) starts[request] = time;
            }
            else if (type == "model.request.attempt_failed")
            {
                if (!AgentSpeedJson.Flag(data, "will_retry")) { if (request is not null) starts.Remove(request); current = null; phase = AgentSpeedPhase.Idle; }
            }
            else if (type == "model.response.completed")
            {
                model = AgentSpeedJson.Text(data, "model") ?? model;
                DateTimeOffset? start = request is not null && starts.Remove(request, out var known) ? known : null;
                var key = request ?? $"{id}:{AgentSpeedJson.Number(root, "seq")}:{time:O}";
                phase = AgentSpeedJson.Text(data, "stop_reason") is "tool_use" or "tool_calls" ? AgentSpeedPhase.Tools : AgentSpeedPhase.Idle;
                if (current == request) current = null;
                return [new("qoder:" + key, id, model, start, time, AgentSpeedJson.Number(data, "output_tokens"), AgentSpeedJson.Number(data, "input_tokens"), UsageSource.Qoder)];
            }
        }
        catch (JsonException) { }
        catch (InvalidOperationException) { }
        return [];
    }
}
