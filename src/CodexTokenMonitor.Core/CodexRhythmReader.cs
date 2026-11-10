using static CodexTokenMonitor.AgentSpeedJson;

namespace CodexTokenMonitor;

internal sealed record CodexMessageHistory(
    DateTimeOffset[] Messages, DateTimeOffset[] TurnStarts, DateTimeOffset UpdatedAt, string Source, int Skipped)
{
    public int CountInRange(DateTimeOffset start, DateTimeOffset end) =>
        end > start ? LowerBound(end) - LowerBound(start) : 0;

    private int LowerBound(DateTimeOffset value)
    {
        int low = 0, high = Messages.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (Messages[middle] < value) low = middle + 1;
            else high = middle;
        }
        return low;
    }
}

internal static class CodexRhythmReader
{
    public static CodexMessageHistory Read(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var home = UsageLogPaths.GetCodexHome();
        var historyPath = Path.Combine(home, "thread_history_1.sqlite");
        var statePath = Path.Combine(home, "state_5.sqlite");
        return File.Exists(historyPath) && File.Exists(statePath)
            ? ReadDatabase(historyPath, statePath, now, cancellationToken)
            : ReadLogs(home, now, cancellationToken);
    }

    private static CodexMessageHistory ReadDatabase(
        string historyPath, string statePath, DateTimeOffset now, CancellationToken token)
    {
        var threads = new Dictionary<string, (string? Source, long Created)>(StringComparer.Ordinal);
        using (var state = new AgentSqliteReadOnly(statePath))
        using (var command = state.Connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, coalesce(thread_source, source), coalesce(created_at_ms, created_at * 1000)
                FROM threads
                """;
            using var rows = command.ExecuteReader();
            while (rows.Read())
            {
                token.ThrowIfCancellationRequested();
                threads[rows.GetString(0)] = (rows.IsDBNull(1) ? null : rows.GetString(1), rows.GetInt64(2));
            }
        }

        using var history = new AgentSqliteReadOnly(historyPath);
        using var transaction = history.Connection.BeginTransaction(deferred: true);
        using var query = history.Connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = """
            SELECT thread_id, item_id, created_at_ms, item_json
            FROM thread_items
            WHERE item_type = 'userMessage'
            ORDER BY thread_id, rollout_ordinal
            """;

        var messages = new List<(DateTimeOffset Stamp, string? ClientId)>();
        var turnStarts = new List<DateTimeOffset>();
        var seenThreads = new HashSet<string>(StringComparer.Ordinal);
        var skipped = 0;
        using (var rows = query.ExecuteReader())
        {
            while (rows.Read())
            {
                token.ThrowIfCancellationRequested();
                var threadId = rows.GetString(0);
                if (!threads.TryGetValue(threadId, out var thread) ||
                    ReadTimestamp(rows.GetInt64(2), now) is not { } stamp)
                {
                    skipped++;
                    continue;
                }

                var first = seenThreads.Add(threadId);
                if (IsBackground(thread.Source) ||
                    first && (thread.Source == "chatgpt_handoff" ||
                        thread.Source == "agent_created_thread" &&
                        rows.GetInt64(2) - thread.Created is >= 0 and <= 60_000))
                    continue;

                try
                {
                    using var document = JsonDocument.Parse(rows.GetString(3));
                    var message = document.RootElement;
                    if (Text(message, "id") != rows.GetString(1) ||
                        !message.TryGetProperty("content", out var content) ||
                        content.ValueKind != JsonValueKind.Array || content.GetArrayLength() == 0)
                    {
                        skipped++;
                        continue;
                    }
                    if (!content.EnumerateArray().Any(block => Text(block, "type") == "text" && IsInjected(Text(block, "text"))))
                        messages.Add((stamp, Text(message, "clientId")));
                }
                catch (JsonException) { skipped++; }
            }
        }

        query.CommandText = "SELECT started_at FROM thread_turns WHERE started_at > 0";
        using (var rows = query.ExecuteReader())
        {
            while (rows.Read())
            {
                token.ThrowIfCancellationRequested();
                var started = rows.GetInt64(0);
                if (ReadTimestamp(started < 100_000_000_000 ? started * 1000 : started, now) is { } stamp)
                    turnStarts.Add(stamp);
            }
        }

        // Item IDs such as item-0 can recur in independent turns. SQLite already
        // enforces the compound item key, and client IDs can identify replays across threads.
        var clientIds = new HashSet<string>(StringComparer.Ordinal);
        var unique = messages.OrderBy(m => m.Stamp)
            .Where(m => string.IsNullOrEmpty(m.ClientId) || clientIds.Add(m.ClientId))
            .Select(m => m.Stamp).ToArray();
        return new(unique, turnStarts.ToArray(), now, "Codex 本机会话库", skipped);
    }

    private static CodexMessageHistory ReadLogs(string home, DateTimeOffset now, CancellationToken token)
    {
        var messages = new HashSet<(string ThreadId, DateTimeOffset Stamp)>();
        var turns = new List<DateTimeOffset>();
        var skipped = 0;
        foreach (var root in UsageLogPaths.GetCodexLogRoots(home))
        {
            foreach (var file in Directory.EnumerateFiles(root, "rollout-*.jsonl", SearchOption.AllDirectories))
            {
                token.ThrowIfCancellationRequested();
                try { ReadLog(file, now, token, messages, turns); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; }
            }
        }
        return new(messages.Select(m => m.Stamp).Order().ToArray(), turns.ToArray(), now, "Codex 本机会话日志", skipped);
    }

    private static void ReadLog(
        string file, DateTimeOffset now, CancellationToken token,
        HashSet<(string ThreadId, DateTimeOffset Stamp)> messages, List<DateTimeOffset> turns)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        string? threadId = null, threadSource = null;
        DateTimeOffset? created = null;
        var firstMessage = true;
        while (reader.ReadLine() is { } line)
        {
            token.ThrowIfCancellationRequested();
            // Ignore output and tool records without parsing their payloads.
            if (!line.Contains("\"session_meta\"", StringComparison.Ordinal) &&
                !line.Contains("\"user_message\"", StringComparison.Ordinal) &&
                !line.Contains("\"task_started\"", StringComparison.Ordinal) &&
                !line.Contains("\"turn_started\"", StringComparison.Ordinal)) continue;

            try
            {
                using var document = JsonDocument.Parse(line);
                var entry = document.RootElement;
                var payload = Object(entry, "payload");
                if (Timestamp(entry) is not { } stamp || stamp < DateTimeOffset.UnixEpoch || stamp > now) continue;
                if (threadId is null && Text(entry, "type") == "session_meta")
                {
                    threadId = Text(payload, "id");
                    threadSource = Text(payload, "thread_source") ?? Text(payload, "source");
                    created = Timestamp(payload) ?? stamp;
                    if (IsBackground(threadSource) || Object(payload, "source").ValueKind == JsonValueKind.Object &&
                        Object(payload, "source").TryGetProperty("subagent", out _)) return;
                    continue;
                }
                if (threadId is null || Text(entry, "type") != "event_msg" || stamp < created) continue;
                switch (Text(payload, "type"))
                {
                    case "task_started":
                    case "turn_started":
                        turns.Add(stamp);
                        break;
                    case "user_message":
                        var seed = firstMessage && (threadSource == "chatgpt_handoff" ||
                            threadSource == "agent_created_thread" && stamp - created <= TimeSpan.FromMinutes(1));
                        firstMessage = false;
                        if (!seed && !IsInjected(Text(payload, "message"))) messages.Add((threadId, stamp));
                        break;
                }
            }
            catch (JsonException)
            {
                // A running session can end with a partially written line.
            }
        }
    }

    private static bool IsBackground(string? source) =>
        source is "automation" or "subagent" or "guardian_review" ||
        source?.StartsWith("{\"subagent\"", StringComparison.Ordinal) == true;

    private static bool IsInjected(string? message)
    {
        var text = message.AsSpan().TrimStart();
        return text.StartsWith("<heartbeat>", StringComparison.Ordinal) ||
               text.StartsWith("<codex_delegation>", StringComparison.Ordinal) ||
               text.StartsWith("<realtime_delegation>", StringComparison.Ordinal);
    }

    private static DateTimeOffset? ReadTimestamp(long milliseconds, DateTimeOffset now) =>
        milliseconds > 0 && milliseconds <= now.ToUnixTimeMilliseconds()
            ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
            : null;
}
