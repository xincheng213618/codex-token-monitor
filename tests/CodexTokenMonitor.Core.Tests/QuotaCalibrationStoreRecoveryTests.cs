using System.Text;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class QuotaCalibrationStoreRecoveryTests
{
    private const string Plan = "Custom plan";
    private static readonly DateTimeOffset Start = new(2026, 9, 2, 0, 0, 0, TimeSpan.FromHours(8));
    private static readonly CodexQuotaCycle Period = new(Start, Start.AddDays(7), Start.AddDays(7), 3, 30m, false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CorruptDatabaseReportsOriginalExceptionAndRecoversAtTheSamePath(bool initiallyCorrupt)
    {
        byte[] healthy;
        using (var seed = new CalibrationFixture())
        {
            Upsert(100m);
            seed.ClearPools();
            healthy = File.ReadAllBytes(seed.DatabasePath);
        }

        using var fixture = new CalibrationFixture();
        if (!initiallyCorrupt) Upsert(100m);
        fixture.ClearPools();
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.DatabasePath)!);
        var corrupt = Encoding.UTF8.GetBytes("invalid existing calibration database");
        File.WriteAllBytes(fixture.DatabasePath, corrupt);
        using (var operation = CacheOperationDiagnostics.Begin())
        {
            var error = Assert.Throws<SqliteException>(() => Load());
            var warning = Assert.Single(operation.Warnings);
            Assert.Equal(fixture.DatabasePath, warning.Path);
            Assert.Equal("LoadExact", warning.Operation);
            Assert.Equal(CacheWarningKind.Corrupt, warning.Kind);
            Assert.Equal(error.Message, warning.Message);
        }
        fixture.ClearPools();
        Assert.Equal(corrupt, File.ReadAllBytes(fixture.DatabasePath));
        File.WriteAllBytes(fixture.DatabasePath, healthy);
        using var recovered = CacheOperationDiagnostics.Begin();
        Assert.Equal(100m, Assert.Single(Load()).AverageFullQuotaCost);
        Upsert(125m);
        Assert.Equal(125m, Assert.Single(Load()).AverageFullQuotaCost);
        Assert.Empty(recovered.Warnings);
    }

    [Fact]
    public void MalformedStoredValueStillThrowsAndCanRecoverWithoutReplacingTheStore()
    {
        using var fixture = new CalibrationFixture();
        Upsert(100m);
        fixture.Execute("UPDATE quota_model_calibrations SET average_full_quota_cost = 'broken'");
        using (var operation = CacheOperationDiagnostics.Begin())
        {
            var error = Assert.Throws<FormatException>(() => Load());
            Assert.Equal(error.Message, Assert.Single(operation.Warnings).Message);
            Assert.Equal("broken", fixture.Scalar("SELECT average_full_quota_cost FROM quota_model_calibrations"));
        }
        fixture.Execute("UPDATE quota_model_calibrations SET average_full_quota_cost = '100'");
        using var recovered = CacheOperationDiagnostics.Begin();
        Assert.Equal(100m, Assert.Single(Load()).AverageFullQuotaCost);
        Assert.Empty(recovered.Warnings);
    }

    [Fact]
    public void FailedBatchUpsertRollsBackEarlierUpdatesAndPreservesSavedCalibration()
    {
        using var fixture = new CalibrationFixture();
        Upsert(100m);
        fixture.Execute("""
            CREATE TRIGGER reject_bad_model BEFORE INSERT ON quota_model_calibrations
            WHEN NEW.model_id = 'bad-model'
            BEGIN SELECT RAISE(ABORT, 'controlled insert failure'); END;
            """);
        using (var failed = CacheOperationDiagnostics.Begin())
        {
            var error = Assert.Throws<SqliteException>(() => QuotaModelCapacityCalibrationStore.Upsert(
                Plan, Period, new[] { Estimate("model-a", 999m), Estimate("bad-model", 500m) }));
            var warning = Assert.Single(failed.Warnings);
            Assert.Equal("Upsert", warning.Operation);
            Assert.Equal(error.Message, warning.Message);
            Assert.Equal("100", fixture.Scalar("SELECT average_full_quota_cost FROM quota_model_calibrations WHERE model_id = 'model-a'"));
            Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM quota_model_calibrations"));
        }
        fixture.Execute("DROP TRIGGER reject_bad_model");
        using var recovered = CacheOperationDiagnostics.Begin();
        Assert.Equal(100m, Assert.Single(Load()).AverageFullQuotaCost);
        Upsert(200m);
        Assert.Equal(200m, Assert.Single(Load()).AverageFullQuotaCost);
        Assert.Empty(recovered.Warnings);
    }

    [Fact]
    public void NewAndExistingEmptyCalibrationTablesRemainValidEmptyResults()
    {
        using var fixture = new CalibrationFixture();
        using var operation = CacheOperationDiagnostics.Begin();
        Assert.Empty(Load());
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM quota_model_calibrations"));
        Upsert(100m);
        fixture.Execute("DELETE FROM quota_model_calibrations");
        Assert.Empty(Load());
        QuotaModelCapacityCalibrationStore.Upsert(Plan, Period, Array.Empty<QuotaModelCapacityEstimate>());
        Assert.Empty(Load());
        Assert.Empty(operation.Warnings);
    }

    [Fact]
    public async Task ConcurrentPathsInitializeAndReadTheirOwnCalibrationTables()
    {
        using var fixture = new CalibrationFixture();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var initialized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrivals = 0;
        var initializedCount = 0;
        async Task Run(string name, decimal amount)
        {
            var root = Path.Combine(fixture.Root, name);
            using var cache = MonitorCachePaths.PushLocalAppDataRoot(root);
            using var logs = UsageLogPaths.PushRoot(Path.Combine(root, "logs"));
            var path = MonitorSettingsDatabase.Path;
            try
            {
                if (Interlocked.Increment(ref arrivals) == 2) ready.SetResult();
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Empty(Load());
                if (Interlocked.Increment(ref initializedCount) == 2) initialized.SetResult();
                await initialized.Task.WaitAsync(TimeSpan.FromSeconds(10));
                for (var index = 0; index < 5; index++)
                {
                    using var operation = CacheOperationDiagnostics.Begin();
                    Upsert(amount + index);
                    await Task.Yield();
                    Assert.Equal(amount + index, Assert.Single(Load()).AverageFullQuotaCost);
                    Assert.Empty(operation.Warnings);
                }
            }
            finally
            {
                ClearPools(path);
            }
        }
        await Task.WhenAll(Task.Run(() => Run("a", 100m)), Task.Run(() => Run("b", 900m)));
        using (MonitorCachePaths.PushLocalAppDataRoot(Path.Combine(fixture.Root, "a")))
        {
            Assert.Equal(104m, Assert.Single(Load()).AverageFullQuotaCost);
            ClearPools(MonitorSettingsDatabase.Path);
        }
        using (MonitorCachePaths.PushLocalAppDataRoot(Path.Combine(fixture.Root, "b")))
        {
            Assert.Equal(904m, Assert.Single(Load()).AverageFullQuotaCost);
            ClearPools(MonitorSettingsDatabase.Path);
        }
    }

    [Fact]
    public void MissingKnownDatabaseReportsFailureWithoutCreatingAReplacement()
    {
        using var fixture = new CalibrationFixture();
        Upsert(100m);
        fixture.ClearPools();
        var backup = fixture.DatabasePath + ".backup";
        File.Move(fixture.DatabasePath, backup);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var failed = CacheOperationDiagnostics.Begin();
            Assert.Throws<SqliteException>(() => Load());
            Assert.Throws<SqliteException>(() => Upsert(999m));
            Assert.Contains(failed.Warnings, warning => warning.Operation == "LoadExact");
            Assert.Contains(failed.Warnings, warning => warning.Operation == "Upsert");
            Assert.False(File.Exists(fixture.DatabasePath));
        }
        fixture.ClearPools();
        File.Move(backup, fixture.DatabasePath);
        using var recovered = CacheOperationDiagnostics.Begin();
        Assert.Equal(100m, Assert.Single(Load()).AverageFullQuotaCost);
        Assert.Empty(recovered.Warnings);
    }

    [Fact]
    public void MissingKnownTableIsNotRecreatedAndRestoredDataCanBeRead()
    {
        using var fixture = new CalibrationFixture();
        Upsert(100m);
        fixture.ClearPools();
        var healthy = File.ReadAllBytes(fixture.DatabasePath);
        fixture.Execute("DROP TABLE quota_model_calibrations");
        using (var failed = CacheOperationDiagnostics.Begin())
        {
            Assert.Throws<SqliteException>(() => Load());
            Assert.Single(failed.Warnings);
        }
        using (var retry = CacheOperationDiagnostics.Begin())
        {
            Assert.Throws<InvalidDataException>(() => Load());
            Assert.Throws<InvalidDataException>(() => Upsert(999m));
            Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name = 'quota_model_calibrations'"));
            Assert.Equal(2, retry.Warnings.Count);
        }
        fixture.ClearPools();
        File.WriteAllBytes(fixture.DatabasePath, healthy);
        using var recovered = CacheOperationDiagnostics.Begin();
        Assert.Equal(100m, Assert.Single(Load()).AverageFullQuotaCost);
        Assert.Empty(recovered.Warnings);
    }

    private static QuotaModelCapacityEstimate Estimate(string model, decimal amount) => new(
        model, 2, amount, amount - 10, amount + 10,
        QuotaModelCapacitySource.CurrentPeriodApproved, Start);

    private static void Upsert(decimal amount) => QuotaModelCapacityCalibrationStore.Upsert(
        Plan, Period, new[] { Estimate("model-a", amount) });

    private static IReadOnlyList<QuotaModelCapacityEstimate> Load() => QuotaModelCapacityCalibrationStore.LoadExact(
        Plan, Start, QuotaModelCapacitySource.CurrentPeriodApproved);

    private static void ClearPools(string path)
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

    private sealed class CalibrationFixture : IDisposable
    {
        private readonly IDisposable cacheScope;
        private readonly IDisposable logScope;
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"CalibrationRecoveryTests-{Guid.NewGuid():N}");
        public string DatabasePath => Path.Combine(Root, "CodexTokenMonitor", "monitor-settings.sqlite3");

        public CalibrationFixture()
        {
            cacheScope = MonitorCachePaths.PushLocalAppDataRoot(Root);
            logScope = UsageLogPaths.PushRoot(Path.Combine(Root, "logs"));
        }

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

        public void ClearPools() => QuotaCalibrationStoreRecoveryTests.ClearPools(DatabasePath);

        public void Dispose()
        {
            ClearPools();
            logScope.Dispose();
            cacheScope.Dispose();
            var resolved = Path.GetFullPath(Root);
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(temp, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolved).StartsWith("CalibrationRecoveryTests-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing to clean up outside the isolated calibration test directory.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        }
    }
}
