using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexTokenMonitor;

/// <summary>
/// Parses Antigravity transcript.jsonl and Gemini CLI session-*.jsonl[json] records for Agent Speed.
/// In Antigravity transcripts, PLANNER_RESPONSE carries the planner step start timestamp and token counts;
/// when tool_calls are present, the immediately following GENERIC tool step's created_at marks generation end.
/// </summary>
internal sealed class GeminiSpeedParser : IAgentSpeedParser
{
    private static readonly ConcurrentDictionary<string, string> SubagentParents = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Regex CompletedAtRegex = new(@"Completed At:\s*([0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9:\.\+\-Z]+)", RegexOptions.Compiled);
    private static readonly Regex ConversationIdRegex = new(@"(?:""conversationId""\s*:\s*""|conversationId\s*[:=]\s*|conversation://)([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})", RegexOptions.Compiled);

    private readonly string id;
    private readonly Func<string, bool>? canUse;
    private readonly bool isCliSession;
    private string session;
    private string model = "未知模型";
    private bool subagent;
    private AgentSpeedPhase phase;
    private DateTimeOffset? last;
    private DateTimeOffset? requestStart;

    private PendingPlannerStep? pendingPlanner;
    private int remainingToolCalls;
    private DateTimeOffset? latestToolCompletedAt;
    private bool expectingSubagentResult;

    private sealed record PendingPlannerStep(
        string Key,
        string Model,
        DateTimeOffset Start,
        long OutputTokens,
        long InputTokens);

    public GeminiSpeedParser(string id, Func<string, bool>? canUse = null)
    {
        this.id = id;
        this.canUse = canUse;
        var fileName = Path.GetFileName(id);
        isCliSession = fileName.StartsWith("session-", StringComparison.OrdinalIgnoreCase);
        session = isCliSession
            ? Path.GetFileNameWithoutExtension(id)
            : GeminiUsageReader.ExtractAntigravityConversationId(id);
    }

    public AgentSpeedAgent Agent
    {
        get
        {
            string? resolvedParent = null;
            if (SubagentParents.TryGetValue(session, out var discoveredParent) &&
                !string.Equals(discoveredParent, session, StringComparison.OrdinalIgnoreCase))
            {
                resolvedParent = discoveredParent;
            }

            return new AgentSpeedAgent(
                id,
                session,
                model,
                subagent || resolvedParent is not null,
                phase,
                last,
                requestStart,
                UsageSource.Gemini,
                isCliSession ? "CLI" : "Antigravity",
                resolvedParent);
        }
    }

    public void Reset()
    {
        model = "未知模型";
        phase = AgentSpeedPhase.Idle;
        last = null;
        requestStart = null;
        pendingPlanner = null;
        remainingToolCalls = 0;
        latestToolCompletedAt = null;
        expectingSubagentResult = false;
    }

    public IReadOnlyList<AgentSpeedResponse> ReadResponses(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return [];

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return [];

            return isCliSession
                ? ReadCliLine(root)
                : ReadAntigravityLine(root);
        }
        catch (JsonException) { }
        catch (InvalidOperationException) { }
        return [];
    }

    private IReadOnlyList<AgentSpeedResponse> ReadAntigravityLine(JsonElement root)
    {
        if (!GeminiUsageReader.TryParseTimestamp(root, "created_at", out var timestamp))
            return [];

        last = last > timestamp ? last : timestamp;
        var type = AgentSpeedJson.Text(root, "type");
        var source = AgentSpeedJson.Text(root, "source");

        if (type == "SYSTEM_MESSAGE")
        {
            var stepIndex = AgentSpeedJson.Number(root, "step_index");
            if (stepIndex == 0)
            {
                subagent = true;
                pendingPlanner = null;
                remainingToolCalls = 0;
                latestToolCompletedAt = null;
                expectingSubagentResult = false;
                requestStart = timestamp;
                phase = AgentSpeedPhase.Generating;
            }

            return [];
        }

        if (type == "USER_INPUT")
        {
            var stepIndex = AgentSpeedJson.Number(root, "step_index");
            var content = AgentSpeedJson.Text(root, "content") ?? "";
            if (GeminiUsageReader.IsExplicitHumanPrompt(source, content))
            {
                subagent = false;
            }
            else if (stepIndex == 0 && GeminiUsageReader.IsSubagentInitialStep(type, stepIndex, source, content))
            {
                subagent = true;
            }

            pendingPlanner = null;
            remainingToolCalls = 0;
            latestToolCompletedAt = null;
            expectingSubagentResult = false;
            requestStart = timestamp;
            phase = AgentSpeedPhase.Generating;
            return [];
        }

        if (type == "PLANNER_RESPONSE")
        {
            var results = new List<AgentSpeedResponse>(2);
            if (pendingPlanner is { } previous && timestamp >= previous.Start)
            {
                results.Add(new AgentSpeedResponse(
                    previous.Key, id, previous.Model, previous.Start, timestamp,
                    previous.OutputTokens, previous.InputTokens, UsageSource.Gemini, Final: true, ExecutionMode: "Antigravity"));
                pendingPlanner = null;
            }

            var nextModel = AgentSpeedJson.Text(root, "model");
            if (!string.IsNullOrWhiteSpace(nextModel)) model = nextModel;

            var uncached = AgentSpeedJson.Number(root, "input_tokens");
            var cached = Math.Max(AgentSpeedJson.Number(root, "cache_read_tokens"), AgentSpeedJson.Number(root, "cached_tokens"));
            var cacheWrite = AgentSpeedJson.Number(root, "cache_write_tokens");
            var input = Math.Max(0, uncached) + Math.Max(0, cached) + Math.Max(0, cacheWrite);
            var output = Math.Max(0, AgentSpeedJson.Number(root, "output_tokens"));

            var stepKey = root.TryGetProperty("step_index", out var stepProp) && stepProp.ValueKind == JsonValueKind.Number && stepProp.TryGetInt64(out var stepVal)
                ? stepVal.ToString(CultureInfo.InvariantCulture)
                : timestamp.UtcTicks.ToString(CultureInfo.InvariantCulture);
            var key = $"agy:{session}:{stepKey}";
            if (canUse?.Invoke(key) == false) return results;

            var toolCallsCount = 0;
            var invokedSubagent = false;
            if (root.TryGetProperty("tool_calls", out var tools) && tools.ValueKind == JsonValueKind.Array)
            {
                foreach (var tool in tools.EnumerateArray())
                {
                    toolCallsCount++;
                    var toolName = AgentSpeedJson.Text(tool, "name");
                    if (toolName == "invoke_subagent") invokedSubagent = true;
                }
            }

            expectingSubagentResult = invokedSubagent;
            latestToolCompletedAt = null;
            remainingToolCalls = toolCallsCount;

            var priorRequestStart = requestStart;
            if (toolCallsCount > 0)
            {
                pendingPlanner = new PendingPlannerStep(key, model, timestamp, output, input);
                phase = AgentSpeedPhase.Tools;
                requestStart = null;
            }
            else
            {
                pendingPlanner = null;
                phase = AgentSpeedPhase.Idle;
                requestStart = null;
            }

            if (input > 0 || output > 0)
            {
                // Emit immediately so request/token counts are present even before the next step arrives;
                // if tool_calls exist, the subsequent GENERIC step upserts Start/End with exact elapsed time.
                DateTimeOffset? fallbackStart = toolCallsCount == 0 && priorRequestStart is { } req && timestamp > req ? req : null;
                results.Add(new AgentSpeedResponse(
                    key, id, model, fallbackStart, timestamp, output, input,
                    UsageSource.Gemini, Final: true, ExecutionMode: "Antigravity"));
            }

            return results;
        }

        if (type == "GENERIC")
        {
            var results = new List<AgentSpeedResponse>(1);
            if (pendingPlanner is { } pending && timestamp >= pending.Start)
            {
                results.Add(new AgentSpeedResponse(
                    pending.Key, id, pending.Model, pending.Start, timestamp,
                    pending.OutputTokens, pending.InputTokens, UsageSource.Gemini, Final: true, ExecutionMode: "Antigravity"));
                pendingPlanner = null;
            }

            var content = AgentSpeedJson.Text(root, "content");
            if (!string.IsNullOrEmpty(content))
            {
                if (expectingSubagentResult)
                {
                    foreach (Match match in ConversationIdRegex.Matches(content))
                    {
                        var childId = match.Groups[1].Value;
                        if (!string.Equals(childId, session, StringComparison.OrdinalIgnoreCase))
                        {
                            SubagentParents[childId] = session;
                        }
                    }
                }

                foreach (Match match in CompletedAtRegex.Matches(content))
                {
                    if (DateTimeOffset.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var completedAt))
                    {
                        var localCompleted = completedAt.ToOffset(CodexUsageReader.BeijingOffset);
                        latestToolCompletedAt = latestToolCompletedAt is { } prev && prev > localCompleted ? prev : localCompleted;
                        last = last > localCompleted ? last : localCompleted;
                    }
                }
            }

            if (remainingToolCalls > 0)
            {
                remainingToolCalls--;
                if (remainingToolCalls == 0)
                {
                    expectingSubagentResult = false;
                    var resumeAt = latestToolCompletedAt ?? timestamp;
                    requestStart = resumeAt;
                    phase = AgentSpeedPhase.Generating;
                }
            }

            return results;
        }

        return [];
    }

    private IReadOnlyList<AgentSpeedResponse> ReadCliLine(JsonElement root)
    {
        session = AgentSpeedJson.Text(root, "sessionId") ?? session;
        var nextModel = AgentSpeedJson.Text(root, "model");
        if (!string.IsNullOrWhiteSpace(nextModel)) model = nextModel;

        if (!GeminiUsageReader.TryParseTimestamp(root, "timestamp", out var timestamp) &&
            !GeminiUsageReader.TryParseTimestamp(root, "createdAt", out timestamp) &&
            !GeminiUsageReader.TryParseTimestamp(root, "created_at", out timestamp))
        {
            return [];
        }

        last = last > timestamp ? last : timestamp;
        var type = AgentSpeedJson.Text(root, "type") ?? AgentSpeedJson.Text(root, "role");
        if (string.Equals(type, "user", StringComparison.OrdinalIgnoreCase))
        {
            requestStart = timestamp;
            phase = AgentSpeedPhase.Generating;
            return [];
        }

        if (GeminiUsageReader.TryExtractGeminiCliTokens(root, out var input, out _, out _, out var output, out _, out _))
        {
            var msgId = AgentSpeedJson.Text(root, "id") ?? AgentSpeedJson.Text(root, "uuid") ?? timestamp.UtcTicks.ToString(CultureInfo.InvariantCulture);
            var key = $"gcli:{session}:{msgId}";
            if (canUse?.Invoke(key) == false) return [];
            var start = requestStart is { } s && timestamp >= s ? s : (DateTimeOffset?)null;
            requestStart = null;
            phase = AgentSpeedPhase.Idle;
            return [new AgentSpeedResponse(key, id, model, start, timestamp, output, input, UsageSource.Gemini, Final: true, ExecutionMode: "CLI")];
        }

        return [];
    }

    /// <summary>
    /// Reads a multi-line Gemini CLI session-*.json file into an AgentSpeedEngine.
    /// Returns true when the file was completely parsed without I/O or JSON errors.
    /// </summary>
    internal static bool ReadSessionJsonFile(
        string file,
        AgentSpeedEngine engine,
        Func<string, bool>? canUse = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return true;

            var sessionId = AgentSpeedJson.Text(root, "sessionId") ?? Path.GetFileNameWithoutExtension(file);
            var currentModel = AgentSpeedJson.Text(root, "model") ?? "未知模型";
            DateTimeOffset? lastActivity = null;
            if (GeminiUsageReader.TryParseTimestamp(root, "lastUpdated", out var updated)) lastActivity = updated;
            else if (GeminiUsageReader.TryParseTimestamp(root, "startTime", out var started)) lastActivity = started;

            DateTimeOffset? pendingUserStart = null;
            var phase = AgentSpeedPhase.Idle;

            if (root.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var message in messages.EnumerateArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var currentIndex = index++;
                    if (message.ValueKind != JsonValueKind.Object) continue;

                    var msgModel = AgentSpeedJson.Text(message, "model");
                    if (!string.IsNullOrWhiteSpace(msgModel)) currentModel = msgModel;

                    DateTimeOffset? msgTime = null;
                    if (GeminiUsageReader.TryParseTimestamp(message, "timestamp", out var ts) ||
                        GeminiUsageReader.TryParseTimestamp(message, "createdAt", out ts) ||
                        GeminiUsageReader.TryParseTimestamp(message, "created_at", out ts))
                    {
                        msgTime = ts;
                        lastActivity = lastActivity is { } prev && prev > ts ? prev : ts;
                    }

                    var type = AgentSpeedJson.Text(message, "type") ?? AgentSpeedJson.Text(message, "role");
                    if (string.Equals(type, "user", StringComparison.OrdinalIgnoreCase))
                    {
                        pendingUserStart = msgTime;
                        phase = AgentSpeedPhase.Generating;
                        continue;
                    }

                    if (GeminiUsageReader.TryExtractGeminiCliTokens(message, out var input, out _, out _, out var output, out _, out _))
                    {
                        var end = msgTime ?? lastActivity;
                        if (end is null) continue;
                        var msgId = AgentSpeedJson.Text(message, "id") ?? AgentSpeedJson.Text(message, "uuid") ?? currentIndex.ToString(CultureInfo.InvariantCulture);
                        var key = $"gcli:{sessionId}:{msgId}";
                        if (canUse?.Invoke(key) != false)
                        {
                            var start = pendingUserStart is { } s && end.Value >= s ? s : (DateTimeOffset?)null;
                            engine.Add(new AgentSpeedResponse(key, file, currentModel, start, end.Value, output, input, UsageSource.Gemini, Final: true, ExecutionMode: "CLI"));
                        }

                        pendingUserStart = null;
                        phase = AgentSpeedPhase.Idle;
                    }
                }
            }

            engine.Update(new AgentSpeedAgent(file, sessionId, currentModel, false, phase, lastActivity, pendingUserStart, UsageSource.Gemini, "CLI"));
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        {
            return false;
        }
    }
}
