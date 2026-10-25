namespace CodexTokenMonitor;

internal static class AgentSpeedJson
{
    internal static string? Text(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    internal static JsonElement Object(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Object ? property : default;
    internal static long Number(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var number)
        ? (long)Math.Clamp(double.IsNaN(number) ? 0 : number, 0, 1_000_000_000_000d) : 0;
    internal static DateTimeOffset? Timestamp(JsonElement value, string name = "timestamp") =>
        DateTimeOffset.TryParse(Text(value, name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp) ? timestamp : null;
    internal static DateTimeOffset? Milliseconds(JsonElement value, string name = "time")
    {
        try
        {
            return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) &&
                property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var milliseconds)
                ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds) : null;
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }
    internal static bool Flag(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.True;
    internal static string? Mode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "unknown" => null,
        "default" or "standard" or "normal" => "Standard",
        "priority" or "fast" => "Fast",
        "ultrafast" or "ultra_fast" => "Ultrafast",
        _ => value!.Trim()
    };
    internal static string? ModeWithEvidence(string? value, string evidence) => Mode(value) is { } mode ? $"{mode}（{evidence}）" : null;
    internal static string? KimiMode(string model) => model is "k3.1-pro-highspeed" or "kimi-for-coding-highspeed" ? "Highspeed（模型变体）" : null;
    internal static bool HasBlock(JsonElement message, string type) => message.ValueKind == JsonValueKind.Object &&
        message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array &&
        content.EnumerateArray().Any(item => Text(item, "type") == type);
}
