using System.Text.Json;

namespace CodexTokenMonitor;

/// <summary>
/// Incrementally indexes human user prompts across Antigravity conversation transcripts
/// and Gemini CLI sessions into a persistent rhythm cache for Gemini Usage Rhythm analysis.
/// </summary>
internal static class GeminiRhythmReader
{
    internal const string LogSource = "Gemini 本机会话日志（Antigravity + CLI）";

    public static CodexMessageHistory? ReadSaved(string? homeOverride = null)
    {
        var home = GeminiUsageReader.GetGeminiHome(homeOverride);
        var snapshot = new CodexRhythmCache(home, GeminiUsageReader.CacheFolder).Load();
        return snapshot.UpdatedAt == default ? null : snapshot.ToHistory();
    }

    public static CodexMessageHistory Read(
        DateTimeOffset now,
        CancellationToken cancellationToken = default,
        string? homeOverride = null)
    {
        var home = GeminiUsageReader.GetGeminiHome(homeOverride);
        var roots = GeminiUsageReader.GetLogRoots(homeOverride);
        return new CodexRhythmCache(home, GeminiUsageReader.CacheFolder).Update(snapshot =>
        {
            ReadLogs(roots, snapshot, cancellationToken);
            snapshot.Source = LogSource;
            snapshot.UpdatedAt = now;
        }, cancellationToken);
    }

    private static void ReadLogs(
        IReadOnlyList<string> roots,
        CodexRhythmSnapshot snapshot,
        CancellationToken token)
    {
        var (files, complete) = GeminiUsageReader.DiscoverLogFiles(roots, token, reportDiagnostics: false);
        var skipped = complete ? 0 : 1;

        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var stream = CodexLogFileIndex.Open(file);
                var signature = CodexLogFileIndex.Identify(file, stream);
                if (snapshot.Logs.GetValueOrDefault(file)?.Signature == signature)
                    continue;

                stream.Position = 0;
                var entry = ReadFileEntry(file, stream, signature, token);
                using var verification = CodexLogFileIndex.Open(file);
                if (signature == CodexLogFileIndex.Identify(file, verification))
                    snapshot.Logs[file] = entry;
                else
                    skipped++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped++;
            }
        }

        snapshot.LogReadFailures = skipped;
    }

    private static CodexRhythmLogEntry ReadFileEntry(
        string file,
        FileStream stream,
        CodexLogFileIndex.Signature signature,
        CancellationToken token)
    {
        var name = Path.GetFileName(file);
        if (name.StartsWith("session-", StringComparison.OrdinalIgnoreCase) &&
            name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            return ReadCliJsonEntry(file, stream, signature, token);
        }

        if (name.StartsWith("session-", StringComparison.OrdinalIgnoreCase) &&
            name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
        {
            return ReadCliJsonlEntry(file, stream, signature, token);
        }

        return ReadAntigravityTranscriptEntry(file, stream, signature, token);
    }

    private static CodexRhythmLogEntry ReadAntigravityTranscriptEntry(
        string file,
        FileStream stream,
        CodexLogFileIndex.Signature signature,
        CancellationToken token)
    {
        using var reader = new StreamReader(stream, leaveOpen: true);
        var messages = new List<CodexRhythmMessage>();
        var turns = new List<DateTimeOffset>();
        var convId = "agy:" + GeminiUsageReader.ExtractAntigravityConversationId(file);
        var isSubagentConversation = false;
        var seenFirstStep = false;

        while (reader.ReadLine() is { } line)
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line) || !line.Contains("USER_INPUT", StringComparison.Ordinal))
                continue;

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                if (AgentSpeedJson.Text(root, "type") != "USER_INPUT") continue;
                if (!GeminiUsageReader.TryParseTimestamp(root, "created_at", out var stamp) ||
                    stamp < DateTimeOffset.UnixEpoch)
                {
                    continue;
                }

                var source = AgentSpeedJson.Text(root, "source");
                var content = AgentSpeedJson.Text(root, "content") ?? "";
                if (!seenFirstStep)
                {
                    seenFirstStep = true;
                    // Delegated subagents receive synthetic initial task prompts rather than explicit user messages.
                    if (source is not null && source != "USER_EXPLICIT" &&
                        !content.Contains("<USER_REQUEST>", StringComparison.Ordinal))
                    {
                        isSubagentConversation = true;
                        break;
                    }
                }

                if (isSubagentConversation) break;

                if (source == "USER_EXPLICIT" || (source is null && content.Contains("<USER_REQUEST>", StringComparison.Ordinal)))
                {
                    var stepIdx = AgentSpeedJson.Number(root, "step_index");
                    turns.Add(stamp);
                    messages.Add(new CodexRhythmMessage(stamp, $"{convId}:{stepIdx}:{stamp.UtcTicks}"));
                }
            }
            catch (JsonException)
            {
                // Partially written trailing line in an active conversation.
            }
        }

        return new CodexRhythmLogEntry(signature, convId, new CodexRhythmThread(messages.ToArray(), turns.ToArray()));
    }

    private static CodexRhythmLogEntry ReadCliJsonEntry(
        string file,
        FileStream stream,
        CodexLogFileIndex.Signature signature,
        CancellationToken token)
    {
        var messages = new List<CodexRhythmMessage>();
        var turns = new List<DateTimeOffset>();
        var sessionId = "gcli:" + Path.GetFileNameWithoutExtension(file);

        try
        {
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (AgentSpeedJson.Text(root, "sessionId") is { Length: > 0 } rawId)
                    sessionId = "gcli:" + rawId;
                if (GeminiUsageReader.TryParseTimestamp(root, "startTime", out var startStamp) &&
                    startStamp >= DateTimeOffset.UnixEpoch)
                {
                    turns.Add(startStamp);
                }

                if (root.TryGetProperty("messages", out var msgArray) && msgArray.ValueKind == JsonValueKind.Array)
                {
                    var idx = 0;
                    foreach (var msg in msgArray.EnumerateArray())
                    {
                        token.ThrowIfCancellationRequested();
                        var cur = idx++;
                        if (msg.ValueKind != JsonValueKind.Object) continue;
                        var type = AgentSpeedJson.Text(msg, "type") ?? AgentSpeedJson.Text(msg, "role");
                        if (!string.Equals(type, "user", StringComparison.OrdinalIgnoreCase)) continue;

                        if (GeminiUsageReader.TryParseTimestamp(msg, "timestamp", out var stamp) ||
                            GeminiUsageReader.TryParseTimestamp(msg, "createdAt", out stamp) ||
                            GeminiUsageReader.TryParseTimestamp(msg, "created_at", out stamp))
                        {
                            if (stamp < DateTimeOffset.UnixEpoch) continue;
                            var id = AgentSpeedJson.Text(msg, "id") ?? cur.ToString();
                            turns.Add(stamp);
                            messages.Add(new CodexRhythmMessage(stamp, $"{sessionId}:{id}"));
                        }
                    }
                }
            }
        }
        catch (JsonException) { }

        return new CodexRhythmLogEntry(signature, sessionId, new CodexRhythmThread(messages.ToArray(), turns.ToArray()));
    }

    private static CodexRhythmLogEntry ReadCliJsonlEntry(
        string file,
        FileStream stream,
        CodexLogFileIndex.Signature signature,
        CancellationToken token)
    {
        using var reader = new StreamReader(stream, leaveOpen: true);
        var messages = new List<CodexRhythmMessage>();
        var turns = new List<DateTimeOffset>();
        var sessionId = "gcli:" + Path.GetFileNameWithoutExtension(file);
        var idx = 0;

        while (reader.ReadLine() is { } line)
        {
            token.ThrowIfCancellationRequested();
            var cur = idx++;
            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                if (AgentSpeedJson.Text(root, "sessionId") is { Length: > 0 } rawId)
                    sessionId = "gcli:" + rawId;

                var type = AgentSpeedJson.Text(root, "type") ?? AgentSpeedJson.Text(root, "role");
                if (!string.Equals(type, "user", StringComparison.OrdinalIgnoreCase)) continue;

                if (GeminiUsageReader.TryParseTimestamp(root, "timestamp", out var stamp) ||
                    GeminiUsageReader.TryParseTimestamp(root, "createdAt", out stamp) ||
                    GeminiUsageReader.TryParseTimestamp(root, "created_at", out stamp))
                {
                    if (stamp < DateTimeOffset.UnixEpoch) continue;
                    var id = AgentSpeedJson.Text(root, "id") ?? cur.ToString();
                    turns.Add(stamp);
                    messages.Add(new CodexRhythmMessage(stamp, $"{sessionId}:{id}"));
                }
            }
            catch (JsonException) { }
        }

        return new CodexRhythmLogEntry(signature, sessionId, new CodexRhythmThread(messages.ToArray(), turns.ToArray()));
    }
}
