using Microsoft.Data.Sqlite;
using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class ZCodeCliUsageDatabaseTests : IDisposable
{
    private static readonly TimeSpan Beijing = TimeSpan.FromHours(8);
    private readonly string root = Path.Combine(Path.GetTempPath(), $"ZCodeDbTests-{Guid.NewGuid():N}");
    private readonly IDisposable logScope;
    private readonly IDisposable cacheScope;

    public ZCodeCliUsageDatabaseTests()
    {
        cacheScope = MonitorCachePaths.PushLocalAppDataRoot(root);
        logScope = UsageLogPaths.PushRoot(root);
    }

    public void Dispose()
    {
        logScope.Dispose();
        cacheScope.Dispose();
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch
        {
            // Best-effort cleanup of the temporary tree.
        }
    }

    private string DbPath => Path.Combine(root, UsageSource.ZCode.ToString(), "db", "db.sqlite");

    private void SeedDatabase(params (string RequestId, DateTimeOffset Completed, long Total)[] rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        connection.Open();
        using (var create = connection.CreateCommand())
        {
            create.CommandText = """
                CREATE TABLE model_usage (
                    id INTEGER PRIMARY KEY,
                    logical_request_id TEXT,
                    model_id TEXT,
                    started_at INTEGER,
                    completed_at INTEGER,
                    input_tokens INTEGER,
                    output_tokens INTEGER,
                    reasoning_tokens INTEGER,
                    cache_creation_input_tokens INTEGER,
                    cache_read_input_tokens INTEGER,
                    computed_total_tokens INTEGER
                );
                """;
            create.ExecuteNonQuery();
        }

        foreach (var (requestId, completed, total) in rows)
        {
            var epoch = completed.ToUnixTimeMilliseconds();
            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO model_usage
                    (logical_request_id, model_id, started_at, completed_at, input_tokens,
                     output_tokens, reasoning_tokens, cache_creation_input_tokens, cache_read_input_tokens, computed_total_tokens)
                VALUES ($id, 'GLM-5.3-Flash', $completed - 60000, $completed, $input, $output, 0, 0, $cacheRead, $total)
                """;
            insert.Parameters.AddWithValue("$id", requestId);
            insert.Parameters.AddWithValue("$completed", epoch);
            insert.Parameters.AddWithValue("$input", total - 200);
            insert.Parameters.AddWithValue("$output", 200L);
            insert.Parameters.AddWithValue("$cacheRead", total - 300);
            insert.Parameters.AddWithValue("$total", total);
            insert.ExecuteNonQuery();
        }
    }

    private void WriteLogRecord(DateTimeOffset at, string requestId)
    {
        var rollout = Path.Combine(root, UsageSource.ZCode.ToString(), "rollout");
        Directory.CreateDirectory(rollout);
        var timestampText = at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
        File.AppendAllText(
            Path.Combine(rollout, "model-io-sess-log.jsonl"),
            "{\"type\":\"model_io\",\"completedAt\":\"" + timestampText + "\",\"requestId\":\"" + requestId +
            "\",\"model\":{\"modelId\":\"GLM-5.3-Flash\"},\"response\":{\"usage\":{\"inputTokens\":8000," +
            "\"cacheReadTokens\":7000,\"cacheWriteTokens\":0,\"outputTokens\":100,\"reasoningTokens\":0,\"totalTokens\":8100}}}\n");
    }

    [Fact]
    public void ReadEvents_MapsDatabaseRowsWithModelAndStableKeys()
    {
        var start = DateTimeOffset.Now.AddMinutes(-60);
        SeedDatabase(("db-1", start.AddMinutes(30), 50_000L));

        var result = ZCodeCliUsageDatabase.ReadEvents(start, start.AddHours(2));

        Assert.True(result.Available);
        Assert.True(result.IsComplete);
        var row = Assert.Single(result.Events);
        Assert.Equal("zcode-db:db-1", row.Key);
        Assert.Equal("GLM-5.3-Flash", row.ModelId);
        Assert.Equal(49_800L, row.InputTokens);
        Assert.Equal(49_700L, row.CachedInputTokens);
        Assert.Equal(200L, row.OutputTokens);
        Assert.Equal(50_000L, row.TotalTokens);
    }

    [Fact]
    public void ReadEvents_MissingDatabase_IsUnavailable()
    {
        var start = DateTimeOffset.Now.AddMinutes(-60);

        var result = ZCodeCliUsageDatabase.ReadEvents(start, start.AddHours(2), CancellationToken.None);

        Assert.False(result.Available);
        Assert.Empty(result.Events);
    }

    [Fact]
    public void Reader_LogEventsAfterDatabaseEarliest_AreExcludedToAvoidDoubleCounting()
    {
        // The db owns everything since its earliest row: a model-io record in
        // that range describes the same call and must not be counted twice,
        // while a log record older than the db's earliest row is genuine
        // backfill for a period the database does not cover.
        var start = DateTimeOffset.Now.AddMinutes(-60);
        SeedDatabase(("db-1", start.AddMinutes(30), 50_000L));
        WriteLogRecord(start.AddMinutes(40), "log-1");
        WriteLogRecord(start.AddMinutes(5), "log-old");

        var rows = ZCodeUsageReader.ReadTransientDetailRows(start, start.AddHours(2));

        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.All(row.ModelUsage.Keys, key => Assert.Equal("GLM-5.3-Flash", key)));
    }

    [Fact]
    public void Reader_WithoutDatabase_StillReadsLogs()
    {
        WriteLogRecord(DateTimeOffset.Now.AddMinutes(-30), "log-only");

        var rows = ZCodeUsageReader.ReadTransientDetailRows(
            DateTimeOffset.Now.AddMinutes(-60), DateTimeOffset.Now.AddMinutes(60));

        var row = Assert.Single(rows);
        Assert.Equal("GLM-5.3-Flash", Assert.Single(row.ModelUsage).Key);
    }
}
