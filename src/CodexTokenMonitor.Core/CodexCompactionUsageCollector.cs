using static CodexTokenMonitor.AgentSpeedJson;

namespace CodexTokenMonitor;

internal readonly record struct CodexCompactionUsage(TokenUsageEvent Event, bool HasLegacy);

// Collect only modern usage that can be linked to a compaction in the owning rollout.
internal sealed class CodexCompactionUsageCollector
{
    private const int LegacyLineLimit = 8;
    private static readonly TimeSpan LegacyTimeLimit = TimeSpan.FromSeconds(2);
    private readonly Dictionary<string, Observation> modern = new(StringComparer.Ordinal);
    private readonly HashSet<string> compacted = new(StringComparer.Ordinal);
    private readonly List<LegacyObservation> legacy = [];
    private string? ownerThread;
    private string? currentTurn;
    private long ordinal;

    internal void Observe(string line, TokenUsageEvent? acceptedLegacy, string? modelId, string? serviceTier)
    {
        ordinal++;
        if (acceptedLegacy is null && !MayContainCompactionEvidence(line)) return;

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var payload = Object(root, "payload");
            var kind = Text(root, "type");
            if (kind == "session_meta")
            {
                // Later metadata can describe the parent whose history was copied into a child.
                ownerThread ??= Identifier(payload, "id");
                return;
            }
            if (ownerThread is null) return;

            switch (kind)
            {
                case "compacted":
                    if (Identifier(payload, "compaction_response_id") is { } compactedResponse)
                        compacted.Add(compactedResponse);
                    break;
                case "token_usage_record":
                    ObserveModern(root, payload, modelId, serviceTier);
                    break;
                case "turn_context":
                    currentTurn = Identifier(payload, "turn_id") ?? currentTurn;
                    break;
                case "event_msg":
                    ObserveEvent(payload, acceptedLegacy);
                    break;
            }
        }
        catch (Exception error) when (error is JsonException or ArgumentOutOfRangeException)
        {
            // Skip incomplete records without disturbing evidence from the rest of the rollout.
        }
    }

    internal IReadOnlyList<CodexCompactionUsage> Resolve()
    {
        var confirmed = compacted.Where(modern.ContainsKey).Select(id => modern[id])
            .OrderBy(record => record.Ordinal).ToArray();
        if (confirmed.Length == 0) return [];

        var signatures = confirmed.Select(record => record.Counters).ToHashSet();
        var possibleResponses = modern.Values.Where(record => signatures.Contains(record.Counters))
            .ToLookup(record => record.Counters);
        var paired = new HashSet<string>(StringComparer.Ordinal);
        foreach (var observation in legacy)
        {
            // Within the owning rollout an explicit response ID is stronger than
            // turn metadata or counters shared between telemetry formats.
            if (observation.ResponseId is { } response && modern.ContainsKey(response))
                paired.Add(response);
        }
        foreach (var observation in legacy.Where(item => item.ResponseId is null && signatures.Contains(item.Counters)))
        {
            // Keep ordinary and already paired records as candidates. Reassigning
            // evidence from a closer request can mistake a compaction for a duplicate.
            if (NearestResponse(observation, possibleResponses[observation.Counters]) is { } nearest)
                paired.Add(nearest);
        }

        return confirmed.Select(record => new CodexCompactionUsage(record.Event, paired.Contains(record.ResponseId))).ToArray();
    }

    private void ObserveModern(JsonElement root, JsonElement payload, string? modelId, string? serviceTier)
    {
        if (Text(payload, "thread_id") != ownerThread || Identifier(payload, "response_id") is not { } response ||
            modern.ContainsKey(response) || Timestamp(root) is not { } stamp)
            return;

        var usage = Object(payload, "usage");
        var value = new TokenUsageEvent(
            stamp.ToOffset(CodexUsageReader.BeijingOffset),
            ReadCounter(usage, "input_tokens"), ReadCounter(usage, "cached_input_tokens"),
            ReadCounter(usage, "output_tokens"), ReadCounter(usage, "reasoning_output_tokens"),
            ReadCounter(usage, "total_tokens"), $"codex-compaction:{ownerThread}:{response}",
            ReadCounter(usage, "cache_write_input_tokens"),
            Model(usage) ?? Model(payload) ?? modelId,
            Text(usage, "service_tier") ?? Text(payload, "service_tier") ?? serviceTier).Normalize();
        if (value.TotalTokens > 0)
            modern.Add(response, new(ordinal, Identifier(payload, "turn_id") ?? currentTurn, response, value));
    }

    private void ObserveEvent(JsonElement payload, TokenUsageEvent? acceptedLegacy)
    {
        var turn = Identifier(payload, "turn_id");
        switch (Text(payload, "type"))
        {
            case "task_started":
            case "turn_started":
                currentTurn = turn;
                break;
            case "task_complete":
            case "turn_complete":
            case "turn_aborted":
                if (turn is null || currentTurn == turn) currentTurn = null;
                break;
            case "token_count":
                if (acceptedLegacy is not null &&
                    (Identifier(payload, "thread_id") is not { } thread || thread == ownerThread))
                {
                    var counters = UsageCounters.From(acceptedLegacy.Normalize());
                    legacy.Add(new(ordinal, turn ?? currentTurn, Identifier(payload, "response_id"), acceptedLegacy.Timestamp, counters));
                }
                break;
        }
    }

    private static string? NearestResponse(LegacyObservation legacy, IEnumerable<Observation> responses)
    {
        string? nearest = null;
        var best = (Lines: long.MaxValue, Ticks: long.MaxValue);
        foreach (var response in responses)
        {
            if (!SameTurn(response.TurnId, legacy.TurnId)) continue;
            // Anonymous token_count updates follow their modern response record.
            // A new request appended later cannot invalidate a previous pairing.
            var score = (Lines: legacy.Ordinal - response.Ordinal,
                Ticks: (response.Event.Timestamp - legacy.Timestamp).Duration().Ticks);
            if (score.Lines is <= 0 or > LegacyLineLimit || score.Ticks > LegacyTimeLimit.Ticks) continue;
            var comparison = score.CompareTo(best);
            if (comparison < 0)
            {
                best = score;
                nearest = response.ResponseId;
            }
            else if (comparison == 0)
            {
                // Equally plausible records do not establish that the compaction was counted.
                nearest = null;
            }
        }

        return nearest;
    }

    private static bool SameTurn(string? first, string? second) => first is null || second is null || first == second;

    private static bool MayContainCompactionEvidence(string line) =>
        line.Contains("\"session_meta\"", StringComparison.Ordinal) ||
        line.Contains("\"compacted\"", StringComparison.Ordinal) ||
        line.Contains("\"token_usage_record\"", StringComparison.Ordinal) ||
        line.Contains("\"turn_context\"", StringComparison.Ordinal) ||
        line.Contains("\"task_started\"", StringComparison.Ordinal) ||
        line.Contains("\"task_complete\"", StringComparison.Ordinal) ||
        line.Contains("\"turn_started\"", StringComparison.Ordinal) ||
        line.Contains("\"turn_complete\"", StringComparison.Ordinal) ||
        line.Contains("\"turn_aborted\"", StringComparison.Ordinal);

    private static string? Identifier(JsonElement payload, string name) =>
        Text(payload, name) is { } text && !string.IsNullOrWhiteSpace(text) ? text : null;

    private static string? Model(JsonElement payload) => Text(payload, "model") ?? Text(payload, "model_name");

    private static long ReadCounter(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var result) ? result : 0;

    private readonly record struct Observation(long Ordinal, string? TurnId, string ResponseId, TokenUsageEvent Event)
    {
        internal UsageCounters Counters => UsageCounters.From(Event);
    }

    private readonly record struct LegacyObservation(long Ordinal, string? TurnId, string? ResponseId,
        DateTimeOffset Timestamp, UsageCounters Counters);

    private readonly record struct UsageCounters(long Input, long Cached, long CacheWrite, long Output, long Reasoning, long Total)
    {
        internal static UsageCounters From(TokenUsageEvent usage) => new(usage.InputTokens, usage.CachedInputTokens,
            usage.CacheWriteInputTokens, usage.OutputTokens, usage.ReasoningOutputTokens, usage.TotalTokens);
    }
}
