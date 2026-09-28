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
                CREATE TABLE IF NOT EXISTS model_usage (
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

    // The CLI never shares an identifier between its two usage trails: ledger
    // rows carry logical_request_id ("msg_<tag>_<uuid>") while model-io log
    // lines carry the provider's request id (a bare uuid). Fixtures must keep
    // those shapes apart — a unified id would paper over the real mismatch.
    private static string LedgerRequestId(string tag) => $"msg_{tag}_{Guid.NewGuid()}";

    private static string LogRequestId() => Guid.NewGuid().ToString();

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
        SeedDatabase((LedgerRequestId("ab12cd34"), start.AddMinutes(30), 50_000L));

        var result = ZCodeCliUsageDatabase.ReadEvents(start, start.AddHours(2));

        Assert.True(result.Available);
        Assert.True(result.IsComplete);
        var row = Assert.Single(result.Events);
        Assert.StartsWith("zcode:msg_ab12cd34_", row.Key);
        Assert.Equal("GLM-5.3-Flash", row.ModelId);
        Assert.Equal(49_800L, row.InputTokens);
        Assert.Equal(49_700L, row.CachedInputTokens);
        Assert.Equal(200L, row.OutputTokens);
        Assert.Equal(50_000L, row.TotalTokens);
    }

    [Fact]
    public void ReadEvents_LedgerStartIsTheGlobalEarliestRow()
    {
        // The era boundary must come from the whole table: a window whose
        // first call is recent is still fully covered when the ledger's first
        // row ever is days older.
        var start = DateTimeOffset.Now.AddMinutes(-60);
        SeedDatabase(
            (LedgerRequestId("old01234"), start.AddDays(-2), 5_000L),
            (LedgerRequestId("new05678"), start.AddMinutes(30), 50_000L));

        var result = ZCodeCliUsageDatabase.ReadEvents(start, start.AddHours(2));

        // The column stores milliseconds, so compare at that precision.
        var expected = DateTimeOffset.FromUnixTimeMilliseconds(start.AddDays(-2).ToUnixTimeMilliseconds());
        Assert.Equal(expected, result.LedgerStart);
        var row = Assert.Single(result.Events);
        Assert.Equal(50_000L, row.TotalTokens);
    }

    [Fact]
    public void ReadEvents_EmptyLedger_HasNoLedgerStart()
    {
        var start = DateTimeOffset.Now.AddMinutes(-60);
        SeedDatabase();

        var result = ZCodeCliUsageDatabase.ReadEvents(start, start.AddHours(2));

        Assert.True(result.Available);
        Assert.Null(result.LedgerStart);
        Assert.Empty(result.Events);
    }

    [Fact]
    public void ReadEvents_TimestampsFollowCompletedAtLikeTheWindowFilter()
    {
        // An anomalous row (started_at after completed_at) must keep the
        // completed_at timestamp: that is the column the window filter and
        // Earliest use, so the event can never leak outside its scan range.
        var start = DateTimeOffset.Now.AddMinutes(-60);
        SeedDatabase((LedgerRequestId("skew0abc"), start.AddMinutes(30), 50_000L));
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
    public void Reader_DropsLogCopiesInLedgerEraAndKeepsPreLedgerLogs()
    {
        // The production double-count: one call trails in both sources under
        // different identifiers, so key-based deduplication never fires and
        // every ledger-era day counted twice. The ledger is authoritative —
        // log records from its era are copies (or calls official statistics
        // do not know either) and are dropped; log records from before the
        // ledger's first row are the only copy and survive.
        var start = DateTimeOffset.Now.AddMinutes(-60);
        SeedDatabase((LedgerRequestId("era1b2c3"), start.AddMinutes(30), 50_000L));
        WriteLogRecord(start.AddMinutes(30), LogRequestId());
        WriteLogRecord(start.AddMinutes(40), LogRequestId());
        WriteLogRecord(start.AddMinutes(5), LogRequestId());

        var rows = ZCodeUsageReader.ReadTransientDetailRows(start, start.AddHours(2));

        Assert.Equal(2, rows.Count);
        Assert.Equal(58_100L, rows.Sum(row => row.TotalTokens));
        var ledgerRow = rows.Single(row => row.TotalTokens == 50_000L);
        Assert.Equal("GLM-5.3-Flash", Assert.Single(ledgerRow.ModelUsage).Key);
    }

    [Fact]
    public void Reader_RescanningCachedDayWithDatabaseNowAvailable_DoesNotDoubleCount()
    {
        // The production double-count chain: a historical day cached from logs
        // while one file was unreadable (incomplete), then rescanned after the
        // database became available. The cached log row and the rescanned
        // ledger row never share a key, so the ledger-only rescan must
        // REPLACE the cached day rather than merge into it.
        var day = DateTimeOffset.Now.AddDays(-2);
        var dayStart = new DateTimeOffset(day.Year, day.Month, day.Day, 0, 0, 0, Beijing);
        var dayEnd = dayStart.AddDays(1);
        WriteLogRecord(dayStart.AddMinutes(30), LogRequestId());
        var blocked = Path.Combine(root, UsageSource.ZCode.ToString(), "rollout", "model-io-locked.jsonl");
        File.WriteAllText(blocked, "{\"type\":\"other\"}\n");
        using (new LockedFile(blocked))
        {
            var incomplete = ZCodeUsageReader.ReadRange(dayStart, dayEnd);
            Assert.Equal(8_100L, incomplete.TotalTokens);
        }

        SeedDatabase((LedgerRequestId("era4d5e6"), dayStart.AddMinutes(30), 50_000L));

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
        SeedDatabase((LedgerRequestId("db01abcd"), start.AddMinutes(30), 50_000L));

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
        WriteLogRecord(DateTimeOffset.Now.AddMinutes(-30), LogRequestId());

        var rows = ZCodeUsageReader.ReadTransientDetailRows(
            DateTimeOffset.Now.AddMinutes(-60), DateTimeOffset.Now.AddMinutes(60));

        var row = Assert.Single(rows);
        Assert.Equal("GLM-5.3-Flash", Assert.Single(row.ModelUsage).Key);
    }

    [Fact]
    public void Reader_LiveDayRescan_FindsLedgerRowCommittedAfterWatermark()
    {
        // The CLI commits a ledger row moments after flushing the log line.
        // A live day that scanned incrementally from its watermark would miss
        // a row whose completion time is already behind it, forever; the
        // ledger-era full-day rescan picks it up on the next pass.
        var dayStart = new DateTimeOffset(
            DateTimeOffset.Now.Year, DateTimeOffset.Now.Month, DateTimeOffset.Now.Day, 0, 0, 0, Beijing);
        var windowEnd = dayStart.AddHours(2);
        SeedDatabase((LedgerRequestId("live1row1"), dayStart.AddMinutes(30), 50_000L));

        var first = ZCodeUsageReader.ReadRange(dayStart, windowEnd);
        Assert.Equal(50_000L, first.TotalTokens);

        // Committed late, but its completed_at lies inside the scanned range.
        SeedDatabase((LedgerRequestId("live2row2"), dayStart.AddMinutes(90), 60_000L));

        var second = ZCodeUsageReader.ReadRange(dayStart, windowEnd);

        Assert.Equal(110_000L, second.TotalTokens);
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
        WriteLogRecord(dayStart.AddMinutes(30), LogRequestId());
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
