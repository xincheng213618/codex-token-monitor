using Microsoft.Data.Sqlite;
using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class CacheOperationDiagnosticsTests
{
    private static readonly DateTimeOffset Day = new(1999, 1, 10, 0, 0, 0, TimeSpan.FromHours(8));
    private static readonly DateOnly Date = DateOnly.FromDateTime(Day.DateTime);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CorruptDatabase_ReportsUnavailableDataAndLeavesOriginalFileIntact(bool quotaStore)
    {
        using var isolated = new IsolatedCache();
        Directory.CreateDirectory(Path.GetDirectoryName(isolated.CachePath)!);
        var original = "This is deliberately not a SQLite database.";
        File.WriteAllText(isolated.CachePath, original);
        using var operation = CacheOperationDiagnostics.Begin();

        if (quotaStore)
        {
            var cache = QuotaSnapshotCacheStore.Load(isolated.Folder);
            Assert.Empty(cache.GetSnapshots(Date));
            Assert.Throws<CacheUnavailableException>(() => cache.Put(Date, new[] { Snapshot() }, true, Day.AddDays(1), propagateErrors: true));
            Assert.Throws<CacheUnavailableException>(() => cache.EnumerateSnapshots(null, null).ToList());
            Assert.Throws<CacheUnavailableException>(() => cache.MergeImportedSnapshots(new[] { Snapshot() }));
        }
        else
        {
            var cache = UsageCacheStore.Load(isolated.Folder);
            Assert.Equal(0, cache.ReadRange(Day, Day.AddDays(1)).Events);
            Assert.Throws<CacheUnavailableException>(() => cache.Put(Bucket(), detailEvents: new[] { UsageEvent() }, propagateErrors: true));
            Assert.Throws<CacheUnavailableException>(() => cache.EnumerateDetailEvents(null, null).ToList());
            Assert.Throws<CacheUnavailableException>(() => cache.MergeImportedDetailEvents(new[] { UsageEvent() }));
        }

        Assert.NotEmpty(operation.Warnings);
        Assert.All(operation.Warnings, warning =>
        {
            Assert.Equal(isolated.CachePath, warning.Path);
            Assert.Equal(CacheWarningKind.Corrupt, warning.Kind);
            Assert.NotEmpty(warning.Operation);
        });
        using var originalFile = new FileStream(isolated.CachePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var originalReader = new StreamReader(originalFile);
        Assert.Equal(original, originalReader.ReadToEnd());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InitializationFailure_RecoversSameStoreOnNextOperation(bool quotaStore)
    {
        using var isolated = new IsolatedCache();
        var blockedDirectory = Path.GetDirectoryName(isolated.CachePath)!;
        File.WriteAllText(blockedDirectory, "temporary test obstruction");
        UsageCacheStore? usageCache = null;
        QuotaSnapshotCacheStore? quotaCache = null;
        using (var failedOperation = CacheOperationDiagnostics.Begin())
        {
            if (quotaStore)
            {
                quotaCache = QuotaSnapshotCacheStore.Load(isolated.Folder);
                Assert.Empty(quotaCache.GetSnapshots(Date));
            }
            else
            {
                usageCache = UsageCacheStore.Load(isolated.Folder);
                Assert.Empty(usageCache.GetDetailEvents(Date));
            }
            Assert.NotEmpty(failedOperation.Warnings);
            File.Delete(blockedDirectory);
            // One query remains conservatively failed even if the obstruction
            // disappears midway through it. A later query can retry once.
            if (quotaStore) quotaCache!.Put(Date, new[] { Snapshot() }, true, Day.AddDays(1));
            else usageCache!.Put(Bucket(), detailEvents: new[] { UsageEvent() });
            Assert.False(File.Exists(isolated.CachePath));
        }

        using var recoveredOperation = CacheOperationDiagnostics.Begin();
        if (quotaStore)
        {
            Assert.Same(quotaCache, QuotaSnapshotCacheStore.Load(isolated.Folder));
            quotaCache!.Put(Date, new[] { Snapshot() }, true, Day.AddDays(1), propagateErrors: true);
            Assert.Single(quotaCache.GetSnapshots(Date));
        }
        else
        {
            Assert.Same(usageCache, UsageCacheStore.Load(isolated.Folder));
            usageCache!.Put(Bucket(), detailEvents: new[] { UsageEvent() }, propagateErrors: true);
            Assert.Equal(1, usageCache.ReadRange(Day, Day.AddDays(1)).Events);
        }
        Assert.Empty(recoveredOperation.Warnings);
    }

    [Fact]
    public void InvalidModelJson_IsReportedInsteadOfSilentlyDiscardingModelCosts()
    {
        using var isolated = new IsolatedCache();
        var cache = UsageCacheStore.Load(isolated.Folder);
        cache.Put(Bucket(), detailEvents: new[] { UsageEvent() }, propagateErrors: true);
        using var connection = isolated.OpenConnection();
        using (var corrupt = connection.CreateCommand())
        {
            corrupt.CommandText = "UPDATE usage_days SET model_usage_json = '[broken'";
            corrupt.ExecuteNonQuery();
        }

        using var operation = CacheOperationDiagnostics.Begin();
        cache.ReadRange(Day, Day.AddDays(1));

        Assert.Contains(operation.Warnings, warning => warning.Kind == CacheWarningKind.Corrupt && warning.Operation == "ReadRange");
        using var inspect = connection.CreateCommand();
        inspect.CommandText = "SELECT model_usage_json FROM usage_days";
        Assert.Equal("[broken", inspect.ExecuteScalar());
    }

    [Fact]
    public void LockedDatabase_WriteFailureIsVisibleAndRecoversAfterLockRelease()
    {
        using var isolated = new IsolatedCache();
        var cache = UsageCacheStore.Load(isolated.Folder);
        cache.Put(Bucket(), detailEvents: new[] { UsageEvent() }, propagateErrors: true);
        using var connection = isolated.OpenConnection();
        using (var writeLock = connection.BeginTransaction())
        using (var failedOperation = CacheOperationDiagnostics.Begin())
        {
            // A real second SQLite writer owns the transaction. The store's
            // normal busy timeout expires; no test override touches live paths.
            cache.Put(Bucket(), detailEvents: new[] { UsageEvent() });
            Assert.Contains(failedOperation.Warnings, warning => warning.Kind == CacheWarningKind.Locked && warning.Operation == "Put");
            Assert.Throws<CacheUnavailableException>(() => cache.Put(Bucket(), detailEvents: new[] { UsageEvent() }, propagateErrors: true));
        }

        using var recoveredOperation = CacheOperationDiagnostics.Begin();
        cache.Put(Bucket(), detailEvents: new[] { UsageEvent() }, propagateErrors: true);
        Assert.Equal(1, cache.ReadRange(Day, Day.AddDays(1)).Events);
        Assert.Empty(recoveredOperation.Warnings);
    }

    [Fact]
    public async Task Diagnostics_AreIsolatedAcrossParallelOperationsAndFlowIntoWorkers()
    {
        using var outer = CacheOperationDiagnostics.Begin();
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(index => Task.Run(async () =>
        {
            using var operation = CacheOperationDiagnostics.Begin();
            await Task.Run(() => CacheOperationDiagnostics.Report($"cache-{index}", "Read", new IOException($"failure-{index}")));
            return operation.Warnings;
        })));

        Assert.Empty(outer.Warnings);
        Assert.Equal("cache-0", Assert.Single(results[0]).Path);
        Assert.Equal("cache-1", Assert.Single(results[1]).Path);
    }

    [Fact]
    public void NestedDiagnostics_RestoreParentAndDeduplicateIdenticalFailures()
    {
        using var outer = CacheOperationDiagnostics.Begin();
        var failure = new SqliteException("database is locked", 5);
        CacheOperationDiagnostics.Report("outer", "Read", failure);
        using (var inner = CacheOperationDiagnostics.Begin())
        {
            CacheOperationDiagnostics.Report("inner", "Read", failure);
            CacheOperationDiagnostics.Report("inner", "Read", failure);
            Assert.Single(inner.Warnings);
        }
        CacheOperationDiagnostics.Report("outer", "Write", failure);
        Assert.Equal(2, outer.Warnings.Count);
        Assert.All(outer.Warnings, warning => Assert.Equal("outer", warning.Path));
    }

    [Fact]
    public void DatabaseState_RetriesAtMostOncePerQueryAndHonorsCancellation()
    {
        var database = new CacheDatabaseState("synthetic-cache");
        var attempts = 0;
        void Fail()
        {
            attempts++;
            throw new IOException("temporarily inaccessible");
        }
        using (var first = CacheOperationDiagnostics.Begin())
        {
            Assert.False(database.EnsureAvailable(Fail));
            Assert.False(database.EnsureAvailable(Fail));
            Assert.Equal(1, attempts);
            Assert.Single(first.Warnings);
        }
        using (var second = CacheOperationDiagnostics.Begin())
        {
            Assert.False(database.EnsureAvailable(Fail));
            Assert.Equal(2, attempts);
            Assert.Single(second.Warnings);
        }
        using var canceledOperation = CacheOperationDiagnostics.Begin();
        Assert.Throws<OperationCanceledException>(() => database.EnsureAvailable(() => throw new OperationCanceledException()));
        Assert.Empty(canceledOperation.Warnings);
    }

    private static TokenUsageEvent UsageEvent() => new(Day.AddHours(9), 100, 50, 10, 0, 110, "synthetic-event");
    private static TokenUsageBucket Bucket()
    {
        var bucket = new TokenUsageBucket { StartLocal = Day };
        bucket.Add(UsageEvent());
        return bucket;
    }
    private static CodexQuotaSnapshot Snapshot() => new(Day.AddHours(9), "codex", "Codex", 10, Day.AddHours(14), 20, Day.AddDays(7));

    private sealed class IsolatedCache : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), $"CodexCacheHealthTests-{Guid.NewGuid():N}");
        private readonly IDisposable cacheScope;
        private readonly IDisposable logScope;
        public string Folder { get; } = "isolated-source";
        public string CachePath => UsageCacheStore.GetCachePath(Folder);

        public IsolatedCache()
        {
            Directory.CreateDirectory(root);
            cacheScope = MonitorCachePaths.PushLocalAppDataRoot(root);
            logScope = UsageLogPaths.PushRoot(Path.Combine(root, "logs"));
        }

        public SqliteConnection OpenConnection()
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = CachePath, Pooling = false }.ToString());
            connection.Open();
            return connection;
        }

        public void Dispose()
        {
            UsageCacheStore.Delete(Folder);
            logScope.Dispose();
            cacheScope.Dispose();
            var resolved = Path.GetFullPath(root);
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resolved).StartsWith("CodexCacheHealthTests-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing to clean up outside the isolated test directory.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        }
    }
}
