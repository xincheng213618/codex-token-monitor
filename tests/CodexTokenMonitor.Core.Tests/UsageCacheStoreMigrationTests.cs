using Microsoft.Data.Sqlite;
using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class UsageCacheStoreMigrationTests : IDisposable
{
    private readonly string folder = $"Migration-{Guid.NewGuid():N}";
    private readonly string root = Path.Combine(Path.GetTempPath(), $"CacheMigration-{Guid.NewGuid():N}");
    private readonly IDisposable cacheScope;

    public UsageCacheStoreMigrationTests()
    {
        cacheScope = MonitorCachePaths.PushLocalAppDataRoot(root);
    }

    public void Dispose()
    {
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

    /// <summary>
    /// Creates a pre-attribution cache: a complete day whose zcode event row
    /// has no model_id, exactly as cached before model attribution existed.
    /// </summary>
    private void SeedLegacyCache()
    {
        var path = UsageCacheStore.GetCachePath(folder);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        connection.Open();
        Execute(connection, """
            CREATE TABLE usage_days (
                date TEXT PRIMARY KEY,
                is_complete INTEGER NOT NULL,
                scanned_through_local TEXT NULL,
                events INTEGER NOT NULL,
                input_tokens INTEGER NOT NULL,
                cached_input_tokens INTEGER NOT NULL,
                uncached_input_tokens INTEGER NOT NULL,
                output_tokens INTEGER NOT NULL,
                reasoning_output_tokens INTEGER NOT NULL,
                total_tokens INTEGER NOT NULL,
                last_token_event_local TEXT NULL,
                long_context_events INTEGER NOT NULL DEFAULT 0,
                long_context_input_tokens INTEGER NOT NULL DEFAULT 0,
                long_context_cached_input_tokens INTEGER NOT NULL DEFAULT 0,
                long_context_output_tokens INTEGER NOT NULL DEFAULT 0,
                peak_input_tokens INTEGER NOT NULL DEFAULT 0,
                peak_cached_input_tokens INTEGER NOT NULL DEFAULT 0,
                peak_output_tokens INTEGER NOT NULL DEFAULT 0,
                cache_write_input_tokens INTEGER NOT NULL DEFAULT 0,
                long_context_cache_write_input_tokens INTEGER NOT NULL DEFAULT 0,
                peak_cache_write_input_tokens INTEGER NOT NULL DEFAULT 0,
                model_usage_json TEXT NOT NULL DEFAULT '{}'
            );
            CREATE TABLE usage_events (
                date TEXT NOT NULL,
                event_key TEXT NOT NULL,
                timestamp_local TEXT NOT NULL,
                input_tokens INTEGER NOT NULL,
                cached_input_tokens INTEGER NOT NULL,
                output_tokens INTEGER NOT NULL,
                reasoning_output_tokens INTEGER NOT NULL,
                total_tokens INTEGER NOT NULL,
                cache_write_input_tokens INTEGER NOT NULL DEFAULT 0,
                model_id TEXT NULL,
                service_tier TEXT NULL,
                PRIMARY KEY (date, event_key)
            );
            INSERT INTO usage_days (date, is_complete, events, input_tokens, cached_input_tokens, uncached_input_tokens, output_tokens, reasoning_output_tokens, total_tokens)
                VALUES ('2026-09-10', 1, 1, 500, 0, 500, 0, 0, 500);
            INSERT INTO usage_events (date, event_key, timestamp_local, input_tokens, cached_input_tokens, output_tokens, reasoning_output_tokens, total_tokens)
                VALUES ('2026-09-10', 'zcode:legacy', '2026-09-10T08:00:00+08:00', 500, 0, 0, 0, 500);
            """);
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    [Fact]
    public void Load_MarksLegacyZcodeDayIncompleteOnce()
    {
        SeedLegacyCache();
        var day = new DateTimeOffset(2026, 9, 10, 0, 0, 0, TimeSpan.FromHours(8));

        _ = UsageCacheStore.Load(folder);
        var incomplete = UsageCacheStore.GetIncompleteDays(folder, day, day.AddDays(1), CancellationToken.None);

        Assert.Contains(day, incomplete);

        // The stale null-model event rows are dropped so the re-scan rebuilds
        // the day from logs with model attribution.
        var dbPath = UsageCacheStore.GetCachePath(folder);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString()))
        {
            connection.Open();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT COUNT(*) FROM usage_events WHERE event_key = 'zcode:legacy'";
                var count = (long)command.ExecuteScalar()!;
                if (count != 0)
                {
                    var markers = new List<string>();
                    using var markerCommand = connection.CreateCommand();
                    markerCommand.CommandText = "SELECT name FROM cache_maintenance";
                    using var reader = markerCommand.ExecuteReader();
                    while (reader.Read()) markers.Add(reader.GetString(0));
                    Assert.Fail(
                        $"zcode:legacy survived (count={count}); " +
                        $"markers=[{string.Join(",", markers)}]; " +
                        $"model_id={File.ReadAllText(dbPath).Length}");
                }
            }
        }
    }

    [Fact]
    public void Load_NonZcodeNullModelDays_AreNotTouchedByZcodeMigration()
    {
        SeedLegacyCache();
        // Rewrite the event as a codex key: the zcode migration must not flag it.
        var path = UsageCacheStore.GetCachePath(folder);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString()))
        {
            connection.Open();
            Execute(connection, "UPDATE usage_events SET event_key = 'codex:legacy'");
        }

        _ = UsageCacheStore.Load(folder);
        var day = new DateTimeOffset(2026, 9, 10, 0, 0, 0, TimeSpan.FromHours(8));
        var incomplete = UsageCacheStore.GetIncompleteDays(folder, day, day.AddDays(1), CancellationToken.None);

        Assert.DoesNotContain(day, incomplete);
    }
}
