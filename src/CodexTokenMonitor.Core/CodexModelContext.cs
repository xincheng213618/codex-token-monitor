namespace CodexTokenMonitor;

// One instance per log/cursor. Model context precedes token_count records and
// must be observed even outside the selected date range and before replay filtering.
internal sealed class CodexModelContext
{
    public string? ModelId { get; private set; }
    public string? ServiceTier { get; private set; }
    private string? sessionId;

    public void Observe(string line)
    {
        if (!line.Contains("\"model\"", StringComparison.Ordinal) && !line.Contains("\"service_tier\"", StringComparison.Ordinal) &&
            !line.Contains("\"session_meta\"", StringComparison.Ordinal)) return;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("payload", out var payload) ||
                payload.ValueKind != JsonValueKind.Object) return;
            var type = Text(root, "type");
            if (type == "session_meta")
            {
                var nextSessionId = Text(payload, "id") ?? Text(payload, "session_id");
                if (sessionId is not null && nextSessionId is not null && sessionId != nextSessionId)
                {
                    ModelId = null;
                    ServiceTier = null;
                }
                sessionId = nextSessionId ?? sessionId;
                // Codex can repeat metadata after turn_context in the same session.
                // That metadata often omits model; absence is not a model change.
                ModelId = Text(payload, "model") ?? ModelId;
                ObserveTier(payload);
            }
            else if (type == "turn_context")
            {
                ModelId = Text(payload, "model") ??
                    (payload.TryGetProperty("info", out var info) ? Text(info, "model") : null) ?? ModelId;
                if (!ObserveTier(payload) && payload.TryGetProperty("info", out var contextInfo)) ObserveTier(contextInfo);
            }
            else if (type == "event_msg" && Text(payload, "type") == "thread_settings_applied" &&
                     payload.TryGetProperty("thread_settings", out var settings))
            {
                ModelId = Text(settings, "model") ?? ModelId;
                ObserveTier(settings);
            }
        }
        catch (JsonException) { }
    }

    private bool ObserveTier(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("service_tier", out var tier)) return false;
        ServiceTier = tier.ValueKind == JsonValueKind.String ? tier.GetString()?.Trim().ToLowerInvariant() : null;
        return true;
    }

    private static string? Text(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.String ? property.GetString() : null;
}
