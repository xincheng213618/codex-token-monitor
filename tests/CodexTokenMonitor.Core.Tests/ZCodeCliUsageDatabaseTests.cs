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
        Assert.Equal("zcode:db-1", row.Key);
        Assert.Equal("GLM-5.3-Flash", row.ModelId);
        Assert.Equal(49_800L, row.InputTokens);
        Assert.Equal(49_700L, row.CachedInputTokens);
        Assert.Equal(200L, row.OutputTokens);
        Assert.Equal(50_000L, row.TotalTokens);
    }

    [Fact]
    public void ReadEvents_TimestampsFollowCompletedAtLikeTheWindowFilter()
    {
        // An anomalous row (started_at after completed_at) must keep the
        // completed_at timestamp: that is the column the window filter and
        // Earliest use, so the event can never leak outside its scan range.
        var start = DateTimeOffset.Now.AddMinutes(-60);
        SeedDatabase(("skew-1", start.AddMinutes(30), 50_000L));
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
               {
                   DataSource = DbPath,
                   Mode = SqliteOpenMode.ReadWrite,
                   Pooling = false
               }.ToString()))
        {
            connection.Open();
            using var update = connection.CreateCommand();
            update.CommandText = "UPDATE model_usage SET started_at = completed_at + 600000";
            update.ExecuteNonQuery();
        }

        var result = ZCodeCliUsageDatabase.ReadEvents(start, start.AddHours(2));

        // The column stores milliseconds, so compare at that precision.
        var completed = DateTimeOffset.FromUnixTimeMilliseconds(start.AddMinutes(30).ToUnixTimeMilliseconds());
        Assert.Equal(completed, Assert.Single(result.Events).Timestamp);
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
    public void Reader_MergesSameRequestAcrossSourcesAndKeepsLogOnlyRequests()
    {
        // Log events and database rows share the stable key zcode:<request id>,
        // so one call read from both sides counts once. A log record with a
        // different request id is a different call and survives even when the
        // database covers the range, and pre-database log records backfill.
        var start = DateTimeOffset.Now.AddMinutes(-60);
        SeedDatabase(("req-1", start.AddMinutes(30), 50_000L));
        WriteLogRecord(start.AddMinutes(30), "req-1");
        WriteLogRecord(start.AddMinutes(40), "log-2");
        WriteLogRecord(start.AddMinutes(5), "log-old");

        var rows = ZCodeUsageReader.ReadTransientDetailRows(start, start.AddHours(2));

        Assert.Equal(3, rows.Count);
        var merged = rows.Single(row => row.TotalTokens == 50_000L);
        Assert.Equal("GLM-5.3-Flash", Assert.Single(merged.ModelUsage).Key);
    }

    [Fact]
    public void Reader_RescanningCachedDayWithDatabaseNowAvailable_DoesNotDoubleCount()
    {
        // The production double-count chain: a historical day cached from logs
        // while one file was unreadable (incomplete), then rescanned after the
        // database became available. Shared stable keys make the cached log
        // row and the rescanned database row replace each other instead of
        // both persisting.
        var day = DateTimeOffset.Now.AddDays(-2);
        var dayStart = new DateTimeOffset(day.Year, day.Month, day.Day, 0, 0, 0, Beijing);
        var dayEnd = dayStart.AddDays(1);
        WriteLogRecord(dayStart.AddMinutes(30), "req-1");
        var blocked = Path.Combine(root, UsageSource.ZCode.ToString(), "rollout", "model-io-locked.jsonl");
        File.WriteAllText(blocked, "{\"type\":\"other\"}\n");
        using (new LockedFile(blocked))
        {
            var incomplete = ZCodeUsageReader.ReadRange(dayStart, dayEnd);
            Assert.Equal(8_100L, incomplete.TotalTokens);
        }

        SeedDatabase(("req-1", dayStart.AddMinutes(30), 50_000L));

        var rescanned = ZCodeUsageReader.ReadRange(dayStart, dayEnd);

        Assert.Equal(50_000L, rescanned.TotalTokens);
        var cachedDetail = Assert.Single(ZCodeUsageReader.ReadCachedDetailRows(dayStart, dayEnd));
        Assert.Equal(50_000L, cachedDetail.TotalTokens);
    }

    private sealed class LockedFile : IDisposable
    {
        private readonly FileStream stream;

        public LockedFile(string path)
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        }

        public void Dispose() => stream.Dispose();
    }

    [Fact]
    public void ReadEvents_MissingDatabase_IsUnavailableWithoutWarning()
    {
        var start = DateTimeOffset.Now.AddMinutes(-60);

        using (var operation = CacheOperationDiagnostics.Begin())
        {
            var result = ZCodeCliUsageDatabase.ReadEvents(start, start.AddHours(2), CancellationToken.None);
            Assert.False(result.Available);
            Assert.False(result.Unreadable);
            Assert.Empty(operation.Warnings);
        }
    }

    [Fact]
    public void ReadEvents_CorruptDatabase_ReportsCorruptWarningAndStaysUnavailable()
    {
        var start = DateTimeOffset.Now.AddMinutes(-60);
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        File.WriteAllText(DbPath, "This is deliberately not a SQLite database.");

        using (var operation = CacheOperationDiagnostics.Begin())
        {
            var result = ZCodeCliUsageDatabase.ReadEvents(start, start.AddHours(2));
            Assert.False(result.Available);
            Assert.True(result.Unreadable);
            var warning = Assert.Single(operation.Warnings);
            Assert.Equal(DbPath, warning.Path);
            Assert.Equal(CacheWarningKind.Corrupt, warning.Kind);
        }
    }

    [Fact]
    public void ReadEvents_LockedDatabase_ReportsWarningAndStaysUnavailable()
    {
        var start = DateTimeOffset.Now.AddMinutes(-60);
        SeedDatabase(("db-1", start.AddMinutes(30), 50_000L));

        using (var operation = CacheOperationDiagnostics.Begin())
        using (new LockedFile(DbPath))
        {
            var result = ZCodeCliUsageDatabase.ReadEvents(start, start.AddHours(2));
            Assert.False(result.Available);
            Assert.True(result.Unreadable);
            var warning = Assert.Single(operation.Warnings);
            Assert.Equal(DbPath, warning.Path);
        }
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

    [Fact]
    public void Reader_WithUnreadableDatabase_DoesNotCacheDayAsCompleteAndHealsLater()
    {
        // A day scanned while the CLI ledger is unreadable must stay
        // incomplete: the CLI rotates logs, so caching it as complete from
        // logs alone would make the missing database rows permanently
        // invisible. Once the database is back the rescan restores completion.
        var day = DateTimeOffset.Now.AddDays(-2);
        var dayStart = new DateTimeOffset(day.Year, day.Month, day.Day, 0, 0, 0, Beijing);
        var dayEnd = dayStart.AddDays(1);
        WriteLogRecord(dayStart.AddMinutes(30), "req-1");
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        File.WriteAllText(DbPath, "This is deliberately not a SQLite database.");

        var blocked = ZCodeUsageReader.ReadRange(dayStart, dayEnd);
        Assert.Equal(8_100L, blocked.TotalTokens);
        Assert.Contains(dayStart, ZCodeUsageReader.GetIncompleteHistoricalDays(dayStart, dayStart));

        File.Delete(DbPath);
        var healed = ZCodeUsageReader.ReadRange(dayStart, dayEnd);
        Assert.Equal(8_100L, healed.TotalTokens);
        Assert.DoesNotContain(dayStart, ZCodeUsageReader.GetIncompleteHistoricalDays(dayStart, dayStart));
    }
}
