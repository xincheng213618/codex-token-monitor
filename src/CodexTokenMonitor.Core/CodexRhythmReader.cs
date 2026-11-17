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
    public static CodexMessageHistory? ReadSaved()
    {
        var snapshot = new CodexRhythmCache(UsageLogPaths.GetCodexHome()).Load();
        return snapshot.UpdatedAt == default ? null : snapshot.ToHistory();
    }

    public static CodexMessageHistory Read(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var home = UsageLogPaths.GetCodexHome();
        var historyPath = Path.Combine(home, "thread_history_1.sqlite");
        var statePath = Path.Combine(home, "state_5.sqlite");
        return new CodexRhythmCache(home).Update(snapshot =>
        {
            if (File.Exists(historyPath) && File.Exists(statePath))
            {
                ReadDatabase(historyPath, statePath, snapshot, cancellationToken);
                snapshot.Source = CodexRhythmCache.DatabaseSource;
            }
            else
            {
                ReadLogs(home, snapshot, cancellationToken);
                snapshot.Source = CodexRhythmCache.LogSource;
            }
            snapshot.UpdatedAt = now;
        }, cancellationToken);
    }

    private static void ReadDatabase(
        string historyPath, string statePath, CodexRhythmSnapshot snapshot, CancellationToken token)
    {
        var threads = new Dictionary<string, CodexRhythmThreadMetadata?>(StringComparer.Ordinal);
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
                threads[rows.GetString(0)] = new(rows.IsDBNull(1) ? null : rows.GetString(1), rows.GetInt64(2));
            }
        }

        var creationTicks = File.GetCreationTimeUtc(historyPath).Ticks;
        using var history = new AgentSqliteReadOnly(historyPath);
        using var transaction = history.Connection.BeginTransaction(deferred: true);
        var projections = ReadProjections(history.Connection, transaction, threads, token);
        using var query = history.Connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = """
            SELECT item_id, created_at_ms, item_json
            FROM thread_items
            WHERE thread_id = $thread AND item_type = 'userMessage'
            ORDER BY rollout_ordinal
            """;
        var itemThread = query.Parameters.Add("$thread", SqliteType.Text);
        using var turnQuery = history.Connection.CreateCommand();
        turnQuery.Transaction = transaction;
        turnQuery.CommandText = "SELECT started_at FROM thread_turns WHERE thread_id = $thread AND started_at > 0";
        var turnThread = turnQuery.Parameters.Add("$thread", SqliteType.Text);

        foreach (var (id, metadata) in threads)
        {
            token.ThrowIfCancellationRequested();
            var projection = projections.GetValueOrDefault(id);
            var previous = snapshot.Database.GetValueOrDefault(id);
            if (previous?.DatabaseCreatedTicks == creationTicks && projection is not null &&
                previous?.Projection == projection && previous.Metadata == metadata)
                continue;
            if (metadata is null && previous?.Metadata is not null) continue;

            itemThread.Value = turnThread.Value = id;
            var entry = ReadDatabaseThread(query, turnQuery, metadata, token);
            // A state row can appear before its history is projected. Keep the
            // previous copy and retry unprojected threads on the next refresh.
            if (projection is null && entry.Messages.Length == 0 && entry.TurnStarts.Length == 0 && previous is not null)
                continue;
            snapshot.Database[id] = new(creationTicks, metadata, projection, entry);
        }
    }

    private static Dictionary<string, CodexRhythmProjection> ReadProjections(
        SqliteConnection connection, SqliteTransaction transaction,
        Dictionary<string, CodexRhythmThreadMetadata?> threads, CancellationToken token)
    {
        var projections = new Dictionary<string, CodexRhythmProjection>(StringComparer.Ordinal);
        using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = "SELECT 1 FROM sqlite_schema WHERE name = 'thread_history_projection_state'";
        var supported = query.ExecuteScalar() is not null;
        query.CommandText = supported
            ? "SELECT thread_id, next_rollout_byte_offset, next_rollout_ordinal FROM thread_history_projection_state"
            : """
              SELECT thread_id FROM thread_items WHERE item_type = 'userMessage'
              UNION SELECT thread_id FROM thread_turns WHERE started_at > 0
              """;
        using var rows = query.ExecuteReader();
        while (rows.Read())
        {
            token.ThrowIfCancellationRequested();
            var id = rows.GetString(0);
            threads.TryAdd(id, null);
            if (supported) projections[id] = new(rows.GetInt64(1), rows.GetInt64(2));
        }
        return projections;
    }

    private static CodexRhythmThread ReadDatabaseThread(
        SqliteCommand query, SqliteCommand turnQuery, CodexRhythmThreadMetadata? metadata, CancellationToken token)
    {
        var messages = new List<CodexRhythmMessage>();
        var turnStarts = new List<DateTimeOffset>();
        var first = true;
        var skipped = 0;
        using (var rows = query.ExecuteReader())
        {
            while (rows.Read())
            {
                token.ThrowIfCancellationRequested();
                var milliseconds = rows.GetInt64(1);
                if (metadata is null || ReadTimestamp(milliseconds) is not { } stamp)
                {
                    skipped++;
                    continue;
                }

                var seed = first && (metadata.Source == "chatgpt_handoff" ||
                    metadata.Source == "agent_created_thread" && milliseconds - metadata.Created is >= 0 and <= 60_000);
                first = false;
                if (seed || IsBackground(metadata.Source))
                    continue;

                try
                {
                    using var document = JsonDocument.Parse(rows.GetString(2));
                    var message = document.RootElement;
                    if (Text(message, "id") != rows.GetString(0) ||
                        !message.TryGetProperty("content", out var content) ||
                        content.ValueKind != JsonValueKind.Array || content.GetArrayLength() == 0)
                    {
                        skipped++;
                        continue;
                    }
                    if (!content.EnumerateArray().Any(block => Text(block, "type") == "text" && IsInjected(Text(block, "text"))))
                        messages.Add(new(stamp, Text(message, "clientId")));
                }
                catch (JsonException) { skipped++; }
            }
        }

        using (var rows = turnQuery.ExecuteReader())
        {
            while (rows.Read())
            {
                token.ThrowIfCancellationRequested();
                var started = rows.GetInt64(0);
                if (ReadTimestamp(started < 100_000_000_000 ? started * 1000 : started) is { } stamp)
                    turnStarts.Add(stamp);
            }
        }

        return new(messages.ToArray(), turnStarts.ToArray(), skipped);
    }

    private static void ReadLogs(string home, CodexRhythmSnapshot snapshot, CancellationToken token)
    {
        var skipped = 0;
        foreach (var root in UsageLogPaths.GetCodexLogRoots(home))
        {
            foreach (var file in Directory.EnumerateFiles(root, "rollout-*.jsonl", SearchOption.AllDirectories))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    using var stream = CodexLogFileIndex.Open(file);
                    var signature = CodexLogFileIndex.Identify(file, stream);
                    if (snapshot.Logs.GetValueOrDefault(file)?.Signature == signature) continue;
                    stream.Position = 0;
                    var entry = ReadLog(stream, signature, token);
                    using var verification = CodexLogFileIndex.Open(file);
                    if (signature == CodexLogFileIndex.Identify(file, verification))
                        snapshot.Logs[file] = entry;
                    else
                        skipped++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; }
            }
        }
        snapshot.LogReadFailures = skipped;
    }

    private static CodexRhythmLogEntry ReadLog(
        FileStream stream, CodexLogFileIndex.Signature signature, CancellationToken token)
    {
        using var reader = new StreamReader(stream, leaveOpen: true);
        var messages = new List<CodexRhythmMessage>();
        var turns = new List<DateTimeOffset>();
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
                if (Timestamp(entry) is not { } stamp || stamp < DateTimeOffset.UnixEpoch) continue;
                if (threadId is null && Text(entry, "type") == "session_meta")
                {
                    threadId = Text(payload, "id");
                    threadSource = Text(payload, "thread_source") ?? Text(payload, "source");
                    created = Timestamp(payload) ?? stamp;
                    if (IsBackground(threadSource) || Object(payload, "source").ValueKind == JsonValueKind.Object &&
                        Object(payload, "source").TryGetProperty("subagent", out _)) break;
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
                        if (!seed && !IsInjected(Text(payload, "message"))) messages.Add(new(stamp));
                        break;
                }
            }
            catch (JsonException)
            {
                // A running session can end with a partially written line.
            }
        }
        return new(signature, threadId, new(messages.ToArray(), turns.ToArray()));
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

    private static DateTimeOffset? ReadTimestamp(long milliseconds) =>
        milliseconds is > 0 and <= 253_402_300_799_999
            ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
            : null;
}
