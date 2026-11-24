using Microsoft.Data.Sqlite;
using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class CodexCompactionCacheReconcileTests : IDisposable
{
    private const string Folder = "CodexTokenMonitor";
    private static readonly DateTimeOffset Day = new(2026, 11, 19, 0, 0, 0, TimeSpan.FromHours(8));
    private static readonly DateOnly Date = DateOnly.FromDateTime(Day.Date);
    private readonly DirectoryInfo root = Directory.CreateTempSubdirectory("CodexCompactionCache-");
    private readonly IDisposable scope;

    public CodexCompactionCacheReconcileTests()
    {
        scope = MonitorCachePaths.PushLocalAppDataRoot(root.FullName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReconcilesAnExactImportedDuplicateAndKeepsUnrelatedEventsAndCoverage(bool useReconciliation)
    {
        var cache = UsageCacheStore.Load(Folder);
        var legacy = Event(1, 100, "legacy");
        var superseded = Event(2, 100, "codex-compaction:superseded");
        var otherImport = Event(3, 70, "other-import");
        var addition = Event(4, 45, "codex-compaction:new");
        var watermark = Day.AddDays(1).AddTicks(-1);
        cache.Put(Bucket(legacy), scannedThroughLocal: watermark, detailEvents: [legacy]);
        cache.MergeImportedDetailEvents([superseded, otherImport]);
        var keys = new HashSet<string>(StringComparer.Ordinal) { superseded.Key! };

        if (useReconciliation)
        {
            Assert.True(cache.ReconcileCodexCompactionDay(Date, [addition], keys));
            Assert.True(cache.ReconcileCodexCompactionDay(Date, [addition], keys));
        }
        else
        {
            cache.Put(Bucket(legacy, otherImport, addition), scannedThroughLocal: watermark,
                detailEvents: [legacy, otherImport, addition], supersededCompactionKeys: keys,
                propagateErrors: true);
        }

        Assert.True(cache.TryGetRecord(Date, out var record));
        Assert.True(record.IsComplete);
        Assert.Equal(watermark, record.ScannedThroughLocal);
        Assert.Equal(3, record.Events);
        Assert.Equal(3, record.DetailEventCount);
        Assert.Equal(215, record.TotalTokens);
        Assert.Equal(new[] { legacy.Key, otherImport.Key, addition.Key },
            cache.GetDetailEvents(Date).Select(item => item.Key));
        Assert.Equal(otherImport.Key, Assert.Single(cache.GetImportedDetailEvents(Date)).Key);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = UsageCacheStore.GetCachePath(Folder),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        connection.Open();
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT event_key FROM imported_usage_event_keys";
        using var marker = query.ExecuteReader();
        Assert.True(marker.Read());
        Assert.Equal(otherImport.Key, marker.GetString(0));
        Assert.False(marker.Read());
    }

    [Fact]
    public void LeavesSummaryOnlyAndMissingDaysUntouched()
    {
        var cache = UsageCacheStore.Load(Folder);
        var legacy = Event(1, 100, "legacy");
        var addition = Event(2, 45, "codex-compaction:new");
        cache.Put(Bucket(legacy));

        Assert.False(cache.ReconcileCodexCompactionDay(Date, [addition]));
        Assert.False(cache.ReconcileCodexCompactionDay(Date.AddDays(1),
            [addition with { Timestamp = addition.Timestamp.AddDays(1) }]));
        Assert.True(cache.TryGetRecord(Date, out var record));
        Assert.Equal(100, record.TotalTokens);
        Assert.Empty(cache.GetDetailEvents(Date));
        Assert.False(cache.TryGetRecord(Date.AddDays(1), out _));
    }

    private static TokenUsageEvent Event(int hour, long tokens, string key) =>
        new(Day.AddHours(hour), tokens, 0, 0, 0, tokens, key, ModelId: "gpt-6.2-sol");

    private static TokenUsageBucket Bucket(params TokenUsageEvent[] events)
    {
        var bucket = new TokenUsageBucket { StartLocal = Day };
        foreach (var item in events) bucket.Add(item);
        return bucket;
    }

    public void Dispose()
    {
        UsageCacheStore.Delete(Folder);
        scope.Dispose();
        var cacheFolder = Path.Combine(root.FullName, Folder);
        if (Directory.Exists(cacheFolder)) Directory.Delete(cacheFolder);
        root.Delete();
    }
}
