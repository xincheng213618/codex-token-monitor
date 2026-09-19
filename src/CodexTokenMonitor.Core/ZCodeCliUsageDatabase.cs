using System.Globalization;
using Microsoft.Data.Sqlite;

namespace CodexTokenMonitor;

/// <summary>
/// Reads the ZCode CLI's own durable usage ledger (db.sqlite, model_usage
/// table). Unlike the model-io rollout logs the CLI rotates, this database
/// keeps every model call, so it is the primary ZCode source; the model-io
/// logs only backfill ranges older than the database's earliest row.
/// </summary>
internal static class ZCodeCliUsageDatabase
{
    public static string DefaultPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".zcode", "cli", "db", "db.sqlite");

    public static string ResolvePath()
    {
        if (UsageLogPaths.GetOverrideRoot(UsageSource.ZCode) is { } overrideRoot)
        {
            return Path.Combine(overrideRoot, "db", "db.sqlite");
        }

        return DefaultPath;
    }

    public sealed record ReadResult(
        bool Available,
        DateTimeOffset? Earliest,
        IReadOnlyList<TokenUsageEvent> Events,
        bool IsComplete);

    /// <summary>
    /// Reads completed model calls whose completion time falls in
    /// [startLocal, endLocal). Available=false means the database is absent or
    /// unreadable and the caller should fall back to log-only scanning.
    /// </summary>
    public static ReadResult ReadEvents(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        CancellationToken cancellationToken = default)
    {
        var path = ResolvePath();
        if (!File.Exists(path))
        {
            return new ReadResult(false, null, Array.Empty<TokenUsageEvent>(), IsComplete: false);
        }

        var events = new List<TokenUsageEvent>();
        DateTimeOffset? earliest = null;
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString());
            connection.Open();
            Execute(connection, "PRAGMA busy_timeout = 2000;");

            // Half-open [start, end) on Unix-millisecond precision (the
            // column's unit) so abutting scan ranges never double-count.
            var startMs = startLocal.ToUnixTimeMilliseconds();
            var endMs = endLocal.ToUnixTimeMilliseconds();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT logical_request_id, model_id, completed_at, started_at,
                       input_tokens, output_tokens, reasoning_tokens,
                       cache_creation_input_tokens, cache_read_input_tokens, computed_total_tokens
                FROM model_usage
                WHERE completed_at IS NOT NULL AND completed_at >= $start AND completed_at < $end
                ORDER BY completed_at
                """;
            command.Parameters.AddWithValue("$start", startMs);
            command.Parameters.AddWithValue("$end", endMs);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var requestId = reader.IsDBNull(0) ? null : reader.GetString(0);
                var modelId = reader.IsDBNull(1) ? null : reader.GetString(1);
                var completedMs = reader.IsDBNull(2) ? 0L : reader.GetInt64(2);
                var startedMs = reader.IsDBNull(3) ? completedMs : reader.GetInt64(3);
                var input = reader.IsDBNull(4) ? 0L : reader.GetInt64(4);
                var output = reader.IsDBNull(5) ? 0L : reader.GetInt64(5);
                var reasoning = reader.IsDBNull(6) ? 0L : reader.GetInt64(6);
                var cacheCreation = reader.IsDBNull(7) ? 0L : reader.GetInt64(7);
                var cacheRead = reader.IsDBNull(8) ? 0L : reader.GetInt64(8);
                var computed = reader.IsDBNull(9) ? 0L : reader.GetInt64(9);
                if (string.IsNullOrWhiteSpace(requestId) || (input == 0 && output == 0 && computed == 0))
                {
                    continue;
                }

                // input_tokens counts the request's total input including cache
                // reads (verified field-by-field against model-io records).
                var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(
                    Math.Max(completedMs, startedMs)).ToOffset(CodexUsageReader.BeijingOffset);
                var cached = Math.Min(input, cacheRead);
                var total = computed > 0
                    ? computed
                    : TokenCountMath.AddNonNegative(
                        TokenCountMath.AddNonNegative(input + cacheCreation, output), reasoning);
                earliest = earliest is { } current && current < timestamp ? current : timestamp;
                events.Add(new TokenUsageEvent(
                    timestamp,
                    InputTokens: input,
                    CachedInputTokens: cached,
                    OutputTokens: output,
                    ReasoningOutputTokens: reasoning,
                    TotalTokens: total,
                    Key: $"zcode-db:{requestId}",
                    CacheWriteInputTokens: cacheCreation,
                    ModelId: modelId));
            }

        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            // The CLI holds the database and may lock or purge it; degrade to
            // log-only scanning instead of failing the whole source.
            return new ReadResult(false, null, Array.Empty<TokenUsageEvent>(), IsComplete: false);
        }

        return new ReadResult(true, earliest, events, IsComplete: true);
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
