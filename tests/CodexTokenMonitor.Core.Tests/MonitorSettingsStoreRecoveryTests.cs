using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class MonitorSettingsStoreRecoveryTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 2, 0, 0, 0, TimeSpan.FromHours(8));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedReadRetainsLastGoodAndRetriesInTheNextOperation(bool resetCards)
    {
        using var fixture = new SettingsFixture();
        Save(resetCards, "original");
        SetStoredStart(fixture, resetCards, "broken");

        using (var operation = CacheOperationDiagnostics.Begin())
        {
            Assert.Equal(new[] { "original" }, LoadIds(resetCards));
            Assert.Single(operation.Warnings);
            SetStoredStart(fixture, resetCards, Start.ToString("O", CultureInfo.InvariantCulture), "repaired");
            // Retrying the same failed operation must not repeatedly touch storage.
            Assert.Equal(new[] { "original" }, LoadIds(resetCards));
            Assert.Single(operation.Warnings);
        }
        using (var recovered = CacheOperationDiagnostics.Begin())
        {
            Assert.Equal(new[] { "repaired" }, LoadIds(resetCards));
            Assert.Empty(recovered.Warnings);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HealthySnapshotsAreReusedOnlyWithinTheOperationUnlessForced(bool resetCards)
    {
        using var fixture = new SettingsFixture();
        Save(resetCards, "first");
        using (var operation = CacheOperationDiagnostics.Begin())
        {
            Assert.Equal(new[] { "first" }, LoadIds(resetCards));
            SetStoredStart(fixture, resetCards, Start.ToString("O", CultureInfo.InvariantCulture), "second");
            Assert.Equal(new[] { "first" }, LoadIds(resetCards));
            Assert.Equal(new[] { "second" }, LoadIds(resetCards, forceReload: true));
            Assert.Empty(operation.Warnings);
        }
        SetStoredStart(fixture, resetCards, Start.ToString("O", CultureInfo.InvariantCulture), "third");
        Assert.Equal(new[] { "second" }, LoadIds(resetCards)); // Scope-free healthy cache remains fast.
        using var next = CacheOperationDiagnostics.Begin();
        Assert.Equal(new[] { "third" }, LoadIds(resetCards));
        Assert.Empty(next.Warnings);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InterleavedOperationsRetainTheirOwnSnapshotsAndForceReloadCanRecover(bool resetCards)
    {
        using var fixture = new SettingsFixture();
        Save(resetCards, "first");
        using var outer = CacheOperationDiagnostics.Begin();
        Assert.Equal(new[] { "first" }, LoadIds(resetCards));
        SetStoredStart(fixture, resetCards, Start.ToString("O", CultureInfo.InvariantCulture), "second");
        using (var inner = CacheOperationDiagnostics.Begin())
        {
            Assert.Equal(new[] { "second" }, LoadIds(resetCards));
            Assert.Empty(inner.Warnings);
        }
        Assert.Equal(new[] { "first" }, LoadIds(resetCards));
        SetStoredStart(fixture, resetCards, "broken");
        Assert.Equal(new[] { "second" }, LoadIds(resetCards, forceReload: true));
        Assert.Single(outer.Warnings);
        SetStoredStart(fixture, resetCards, Start.ToString("O", CultureInfo.InvariantCulture), "third");
        Assert.Equal(new[] { "second" }, LoadIds(resetCards));
        Assert.Equal(new[] { "third" }, LoadIds(resetCards, forceReload: true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnreadableExistingRowsCannotBeReplacedAndDoNotBecomeDefaults(bool resetCards)
    {
        using var fixture = new SettingsFixture();
        CreateRawSettings(fixture, resetCards, invalidDate: true);
        using (var operation = CacheOperationDiagnostics.Begin())
        {
            Assert.Empty(LoadIds(resetCards));
            Assert.Single(operation.Warnings);
        }
        using (var write = CacheOperationDiagnostics.Begin())
        {
            Assert.Throws<FormatException>(() => Save(resetCards, "replacement"));
            Assert.Single(write.Warnings);
            Assert.Equal("stored", fixture.Scalar($"SELECT id FROM {TableName(resetCards)}"));
            Assert.Equal("broken", fixture.Scalar($"SELECT {StartColumn(resetCards)} FROM {TableName(resetCards)}"));
        }
        SetStoredStart(fixture, resetCards, Start.ToString("O", CultureInfo.InvariantCulture));
        using var recovered = CacheOperationDiagnostics.Begin();
        Assert.Equal(new[] { "stored" }, LoadIds(resetCards));
        Assert.Empty(recovered.Warnings);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedWriteRollsBackAndKeepsTheLastSuccessfulSnapshot(bool resetCards)
    {
        using var fixture = new SettingsFixture();
        Save(resetCards, "original");
        using var operation = CacheOperationDiagnostics.Begin();
        Assert.Throws<SqliteException>(() => Save(resetCards, "duplicate", "duplicate"));
        Assert.Contains(operation.Warnings, warning => warning.Operation.EndsWith(".Save", StringComparison.Ordinal));
        Assert.Equal("original", fixture.Scalar($"SELECT id FROM {TableName(resetCards)}"));
        Assert.Equal(new[] { "original" }, LoadIds(resetCards));
        using var recovery = CacheOperationDiagnostics.Begin();
        Assert.Equal(new[] { "original" }, LoadIds(resetCards));
        Assert.Empty(recovery.Warnings);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CorruptDatabasePreservesFileAndRecoversAtTheSamePath(bool resetCards)
    {
        using var fixture = new SettingsFixture();
        Save(resetCards, "original");
        fixture.ClearPool();
        var healthy = File.ReadAllBytes(fixture.DatabasePath);
        var corrupt = Encoding.UTF8.GetBytes("broken settings database");
        File.WriteAllBytes(fixture.DatabasePath, corrupt);
        using (var failed = CacheOperationDiagnostics.Begin())
        {
            Assert.Equal(new[] { "original" }, LoadIds(resetCards));
            Assert.Contains(failed.Warnings, warning => warning.Kind == CacheWarningKind.Corrupt);
            Assert.Throws<SqliteException>(() => Save(resetCards, "replacement"));
            fixture.ClearPool();
            Assert.Equal(corrupt, File.ReadAllBytes(fixture.DatabasePath));
        }
        fixture.ClearPool();
        File.WriteAllBytes(fixture.DatabasePath, healthy);
        SetStoredStart(fixture, resetCards, Start.ToString("O", CultureInfo.InvariantCulture), "restored");
        using var recovered = CacheOperationDiagnostics.Begin();
        Assert.Equal(new[] { "restored" }, LoadIds(resetCards));
        Assert.Empty(recovered.Warnings);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingKnownDatabaseIsNotRecreatedAndRecoversWhenRestored(bool resetCards)
    {
        using var fixture = new SettingsFixture();
        Save(resetCards, "original");
        fixture.ClearPool();
        var backup = fixture.DatabasePath + ".backup";
        File.Move(fixture.DatabasePath, backup);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var operation = CacheOperationDiagnostics.Begin();
            Assert.Equal(new[] { "original" }, LoadIds(resetCards));
            Assert.Single(operation.Warnings);
            Assert.Throws<SqliteException>(() => Save(resetCards, "replacement"));
            Assert.False(File.Exists(fixture.DatabasePath));
        }
        fixture.ClearPool();
        File.Move(backup, fixture.DatabasePath);
        SetStoredStart(fixture, resetCards, Start.ToString("O", CultureInfo.InvariantCulture), "restored");
        using var recovered = CacheOperationDiagnostics.Begin();
        Assert.Equal(new[] { "restored" }, LoadIds(resetCards));
        Assert.Empty(recovered.Warnings);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingKnownTableIsNotRecreatedOrSeeded(bool resetCards)
    {
        using var fixture = new SettingsFixture();
        Save(resetCards, "original");
        fixture.Execute($"DROP TABLE {TableName(resetCards)}");
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var operation = CacheOperationDiagnostics.Begin();
            Assert.Equal(new[] { "original" }, LoadIds(resetCards));
            Assert.Single(operation.Warnings);
            Assert.Throws<InvalidDataException>(() => Save(resetCards, "replacement"));
            Assert.Equal(0L, fixture.Scalar($"SELECT COUNT(*) FROM sqlite_master WHERE name = '{TableName(resetCards)}'"));
        }
        CreateRawSettings(fixture, resetCards, invalidDate: false);
        using var recovered = CacheOperationDiagnostics.Begin();
        Assert.Equal(new[] { "stored" }, LoadIds(resetCards));
        Assert.Empty(recovered.Warnings);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnreadableExistingDatabaseIsStillKnownAfterItsFirstFailure(bool resetCards)
    {
        using var fixture = new SettingsFixture();
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.DatabasePath)!);
        File.WriteAllText(fixture.DatabasePath, "invalid existing user settings");
        using (var failed = CacheOperationDiagnostics.Begin())
        {
            Assert.Empty(LoadIds(resetCards));
            Assert.Single(failed.Warnings);
        }
        fixture.ClearPool();
        File.Move(fixture.DatabasePath, fixture.DatabasePath + ".unreadable");
        using var next = CacheOperationDiagnostics.Begin();
        Assert.Empty(LoadIds(resetCards));
        Assert.Single(next.Warnings);
        Assert.False(File.Exists(fixture.DatabasePath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExistingEmptyTableAndExplicitClearRemainEmpty(bool resetCards)
    {
        using var fixture = new SettingsFixture();
        CreateRawSettings(fixture, resetCards, invalidDate: false);
        fixture.Execute($"DELETE FROM {TableName(resetCards)}");
        Assert.Empty(LoadIds(resetCards, forceReload: true));
        Save(resetCards, "custom");
        Save(resetCards);
        using var operation = CacheOperationDiagnostics.Begin();
        Assert.Empty(LoadIds(resetCards));
        Assert.Equal(0L, fixture.Scalar($"SELECT COUNT(*) FROM {TableName(resetCards)}"));
        Assert.Empty(operation.Warnings);
    }

    [Fact]
    public void MutatingReturnedRecordsDoesNotChangeTheCachedSettings()
    {
        using var fixture = new SettingsFixture();
        Save(false, "plan");
        Save(true, "card");
        using var operation = CacheOperationDiagnostics.Begin();
        Assert.Single(SubscriptionPlanStore.Load()).Id = "changed";
        Assert.Single(ResetOpportunityStore.Load()).Note = "changed";
        Assert.Equal("plan", Assert.Single(SubscriptionPlanStore.Load()).Id);
        Assert.Equal("custom", Assert.Single(ResetOpportunityStore.Load()).Note);
        Assert.Empty(operation.Warnings);
    }

    [Fact]
    public async Task ConcurrentRootsNeverShareSuccessfulOrFailedSnapshots()
    {
        using var fixture = new SettingsFixture();
        var rootA = Path.Combine(fixture.Root, "a");
        var rootB = Path.Combine(fixture.Root, "b");
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readyCount = 0;
        async Task Work(string root, string id)
        {
            using var pathScope = MonitorCachePaths.PushLocalAppDataRoot(root);
            Save(false, id);
            Save(true, id);
            if (Interlocked.Increment(ref readyCount) == 2) ready.SetResult();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            for (var index = 0; index < 5; index++)
            {
                using var operation = CacheOperationDiagnostics.Begin();
                Assert.Equal(new[] { id }, LoadIds(false));
                Assert.Equal(new[] { id }, LoadIds(true));
                Assert.Empty(operation.Warnings);
                await Task.Yield();
            }
            ClearPool(MonitorSettingsDatabase.Path);
        }
        await Task.WhenAll(Task.Run(() => Work(rootA, "a")), Task.Run(() => Work(rootB, "b")));
        using (MonitorCachePaths.PushLocalAppDataRoot(rootA))
        {
            using var connection = MonitorSettingsDatabase.OpenConnection(MonitorSettingsDatabase.Path);
            MonitorSettingsDatabase.ExecuteNonQuery(connection, null, "UPDATE subscription_plans SET start_local = 'broken'");
            using var failure = CacheOperationDiagnostics.Begin();
            Assert.Equal(new[] { "a" }, LoadIds(false));
            Assert.Single(failure.Warnings);
            ClearPool(MonitorSettingsDatabase.Path);
        }
        using (MonitorCachePaths.PushLocalAppDataRoot(rootB))
        using (var healthy = CacheOperationDiagnostics.Begin())
        {
            Assert.Equal(new[] { "b" }, LoadIds(false));
            Assert.Empty(healthy.Warnings);
            ClearPool(MonitorSettingsDatabase.Path);
        }
    }

    [Fact]
    public void FailedSeedRollsBackSchemaAndCanRetryWithoutReplacingTheTableStore()
    {
        using var fixture = new SettingsFixture();
        var seedAttempts = 0;
        var table = CreateTestTable(() =>
        {
            if (++seedAttempts == 1) throw new IOException("controlled seed failure");
            return new[] { "seeded" };
        });
        using (var failed = CacheOperationDiagnostics.Begin())
        {
            Assert.Empty(table.Load());
            Assert.Empty(table.Load());
            Assert.Equal(1, seedAttempts);
            Assert.Single(failed.Warnings);
            Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name = 'test_settings'"));
        }
        using var recovered = CacheOperationDiagnostics.Begin();
        Assert.Equal(new[] { "seeded" }, table.Load());
        Assert.Equal(2, seedAttempts);
        Assert.Empty(recovered.Warnings);
    }

    [Fact]
    public void FailedFirstReadAfterSeedDoesNotCacheAnEmptySuccessOrSeedAgain()
    {
        using var fixture = new SettingsFixture();
        var seedCount = 0;
        var readCount = 0;
        var table = CreateTestTable(
            () => { seedCount++; return new[] { "seeded" }; },
            () => { if (++readCount == 1) throw new IOException("controlled read failure"); });
        using (var failed = CacheOperationDiagnostics.Begin())
        {
            Assert.Empty(table.Load());
            Assert.Single(failed.Warnings);
            Assert.Equal("seeded", fixture.Scalar("SELECT value FROM test_settings"));
        }
        using var recovered = CacheOperationDiagnostics.Begin();
        Assert.Equal(new[] { "seeded" }, table.Load());
        Assert.Equal(1, seedCount);
        Assert.Empty(recovered.Warnings);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"credits\":null}")]
    [InlineData("{\"credits\":{}}")]
    [InlineData("{\"credits\":[null]}")]
    [InlineData("{\"credits\":[{}]}")]
    [InlineData("{\"credits\":[{\"granted_at\":\"bad\",\"expires_at\":\"2026-10-02T00:00:00Z\",\"status\":\"available\"}]}")]
    [InlineData("{\"credits\":[{\"granted_at\":\"2026-09-02T00:00:00Z\",\"expires_at\":\"2026-10-02T00:00:00Z\"}]}")]
    [InlineData("{\"credits\":[{\"granted_at\":\"2026-10-02T00:00:00Z\",\"expires_at\":\"2026-09-02T00:00:00Z\",\"status\":\"available\"}]}")]
    public void MalformedResetResponsesDoNotOverwriteLocalCards(string json)
    {
        using var fixture = new SettingsFixture();
        Save(true, "local-card");
        using var document = JsonDocument.Parse(json);
        Assert.Throws<JsonException>(() => ResetOpportunityStore.ReadApiRecords(document.RootElement));
        Assert.Equal(new[] { "local-card" }, LoadIds(true, forceReload: true));
    }

    [Fact]
    public void ValidResetResponsesKeepStatusAndPermitAnExplicitEmptyArray()
    {
        using var document = JsonDocument.Parse("""
            {"credits":[
              {"id":"a","granted_at":"2026-09-02T00:00:00Z","expires_at":"2026-10-02T00:00:00Z","status":"available"},
              {"id":"b","granted_at":"2026-09-02T00:00:00Z","expires_at":"2026-10-02T00:00:00Z","status":"redeemed","redeemed_at":"2026-09-03T00:00:00Z"}
            ]}
            """);
        var records = ResetOpportunityStore.ReadApiRecords(document.RootElement);
        Assert.Equal(2, records.Count);
        Assert.False(records[0].IsUsed);
        Assert.True(records[1].IsUsed);
        Assert.Equal(TimeSpan.FromHours(8), records[0].GrantedLocal.Offset);
        using var empty = JsonDocument.Parse("{\"credits\":[]}");
        Assert.Empty(ResetOpportunityStore.ReadApiRecords(empty.RootElement));
    }

    private static MonitorSettingsTable<string> CreateTestTable(Func<IReadOnlyList<string>> seed, Action? beforeRead = null) => new(
        "test_settings",
        "CREATE TABLE IF NOT EXISTS test_settings (value TEXT NOT NULL);",
        (connection, transaction) =>
        {
            beforeRead?.Invoke();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT value FROM test_settings";
            using var reader = command.ExecuteReader();
            var records = new List<string>();
            while (reader.Read()) records.Add(reader.GetString(0));
            return records;
        },
        (connection, transaction, records) =>
        {
            MonitorSettingsDatabase.ExecuteNonQuery(connection, transaction, "DELETE FROM test_settings");
            foreach (var record in records)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "INSERT INTO test_settings(value) VALUES ($value)";
                command.Parameters.AddWithValue("$value", record);
                command.ExecuteNonQuery();
            }
        },
        seed,
        records => records.ToArray(),
        records => records.ToArray());

    private static string TableName(bool resetCards) => resetCards ? "reset_opportunities" : "subscription_plans";
    private static string StartColumn(bool resetCards) => resetCards ? "granted_local" : "start_local";
    private static string[] LoadIds(bool resetCards, bool forceReload = false) => resetCards
        ? ResetOpportunityStore.Load(forceReload).Select(item => item.Id).ToArray()
        : SubscriptionPlanStore.Load(forceReload).Select(item => item.Id).ToArray();

    private static void Save(bool resetCards, params string[] ids)
    {
        if (resetCards)
            ResetOpportunityStore.Save(ids.Select(id => new ResetOpportunityRecord
            {
                Id = id, GrantedLocal = Start, ExpiresLocal = Start.AddDays(30), Note = "custom"
            }).ToArray());
        else
            SubscriptionPlanStore.Save(ids.Select(id => new SubscriptionPlanRecord
            {
                Id = id, StartLocal = Start, EndLocal = Start.AddMonths(1), PlanName = "Custom", AmountCny = 17m
            }).ToArray());
    }

    private static void SetStoredStart(SettingsFixture fixture, bool resetCards, string value, string? id = null)
    {
        using var connection = MonitorSettingsDatabase.OpenConnection(fixture.DatabasePath);
        using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE {TableName(resetCards)} SET {StartColumn(resetCards)} = $start, id = COALESCE($id, id)";
        command.Parameters.AddWithValue("$start", value);
        command.Parameters.AddWithValue("$id", (object?)id ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static void CreateRawSettings(SettingsFixture fixture, bool resetCards, bool invalidDate)
    {
        fixture.Execute(resetCards
            ? "CREATE TABLE reset_opportunities (id TEXT PRIMARY KEY, granted_local TEXT NOT NULL, expires_local TEXT NOT NULL, is_used INTEGER NOT NULL, note TEXT NOT NULL)"
            : "CREATE TABLE subscription_plans (id TEXT PRIMARY KEY, start_local TEXT NOT NULL, end_local TEXT NOT NULL, plan_name TEXT NOT NULL, amount_cny TEXT NOT NULL)");
        using var connection = MonitorSettingsDatabase.OpenConnection(fixture.DatabasePath);
        using var command = connection.CreateCommand();
        command.CommandText = resetCards
            ? "INSERT INTO reset_opportunities VALUES ('stored', $start, $end, 0, 'custom')"
            : "INSERT INTO subscription_plans VALUES ('stored', $start, $end, 'Custom', '17')";
        command.Parameters.AddWithValue("$start", invalidDate ? "broken" : Start.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$end", Start.AddMonths(1).ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    private static void ClearPool(string path)
    {
        foreach (var mode in new[] { SqliteOpenMode.ReadWriteCreate, SqliteOpenMode.ReadWrite })
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path, Mode = mode, Pooling = true
            }.ToString());
            SqliteConnection.ClearPool(connection);
        }
    }

    private sealed class SettingsFixture : IDisposable
    {
        private readonly IDisposable scope;
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"SettingsRecoveryTests-{Guid.NewGuid():N}");
        public string DatabasePath => Path.Combine(Root, "CodexTokenMonitor", "monitor-settings.sqlite3");

        public SettingsFixture() => scope = MonitorCachePaths.PushLocalAppDataRoot(Root);

        public void Execute(string sql)
        {
            using var connection = MonitorSettingsDatabase.OpenConnection(DatabasePath);
            MonitorSettingsDatabase.ExecuteNonQuery(connection, null, sql);
        }

        public object? Scalar(string sql)
        {
            using var connection = MonitorSettingsDatabase.OpenConnection(DatabasePath);
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return command.ExecuteScalar();
        }

        public void ClearPool() => MonitorSettingsStoreRecoveryTests.ClearPool(DatabasePath);

        public void Dispose()
        {
            ClearPool();
            scope.Dispose();
            var resolved = Path.GetFullPath(Root);
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(temp, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolved).StartsWith("SettingsRecoveryTests-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing to clean up outside the isolated settings test directory.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        }
    }
}
