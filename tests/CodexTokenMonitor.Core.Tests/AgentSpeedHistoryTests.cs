using System.Text;
using System.Text.Json;
using CodexTokenMonitor;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CodexTokenMonitor.Core.Tests;

public sealed class AgentSpeedHistoryTests
{
    private static readonly DateTimeOffset Day = new(2026, 10, 25, 0, 0, 0, TimeSpan.FromHours(8));
    private static readonly DateOnly Date = DateOnly.FromDateTime(Day.DateTime);
    private static AgentSpeedResponse Response(string key, string file, int output = 300, string? mode = null, int day = 0) =>
        new(key, file, "model", Day.AddDays(day).AddSeconds(1), Day.AddDays(day).AddSeconds(4), output, 900, ExecutionMode: mode);

    [Fact]
    public void RestartReusesUnchangedFilesDeletedLogsRetainHistoryAndCopiesDoNotDuplicate()
    {
        using var fixture = new Fixture(); var store = fixture.Store;
        var first = new AgentSpeedHistoryFile(Path.Combine(fixture.Root, "one"), "v1"); var copy = new AgentSpeedHistoryFile(Path.Combine(fixture.Root, "copy"), "v1");
        AgentSpeedHistoryFileResult Read(AgentSpeedHistoryFile file, CancellationToken _) => new([Response("r", file.Path)], true);
        store.Index(UsageSource.Codex, [first], Read, Day.AddHours(1), default);
        var reopened = new AgentSpeedHistoryStore(store.Path);
        var reused = reopened.Index(UsageSource.Codex, [first], (_, _) => throw new Exception("must reuse"), Day.AddHours(1), default);
        Assert.Equal(1, reused.Reused);
        reopened.Index(UsageSource.Codex, [copy], Read, Day.AddHours(1), default);
        var row = Assert.Single(reopened.Read(Date, Date));
        Assert.Equal(1, row.Requests); Assert.Equal(300, row.OutputTokens); Assert.Equal(100, row.Average(AgentSpeedMetric.Output));
        reopened.Index(UsageSource.Codex, [], Read, Day.AddHours(1), default);
        Assert.Equal(1, Assert.Single(reopened.Read(Date, Date)).Requests);
    }

    [Fact]
    public void ChangedFilesTransferOwnershipAtomicallyAndKeepModesSeparate()
    {
        using var fixture = new Fixture(); var store = fixture.Store;
        var a = new AgentSpeedHistoryFile(Path.Combine(fixture.Root, "a"), "v1"); var b = new AgentSpeedHistoryFile(Path.Combine(fixture.Root, "b"), "v1");
        store.Index(UsageSource.Codex, [a, b], (file, _) => new([Response(file == a ? "r1" : "r2", file.Path, mode: "Fast")], true), Day.AddHours(1), default);
        var changedA = a with { Signature = "v2" }; var changedB = b with { Signature = "v2" };
        store.Index(UsageSource.Codex, [changedB, changedA], (file, _) =>
            new([Response(file == changedA ? "r2" : "r1", file.Path, output: 600, mode: file == changedA ? "Fast" : "Standard")], true), Day.AddHours(1), default);
        var rows = store.Read(Date, Date);
        Assert.Equal(2, rows.Sum(r => r.Requests)); Assert.Equal(1200, rows.Sum(r => r.OutputTokens)); Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal(200, row.Average(AgentSpeedMetric.Output)));
    }

    [Fact]
    public void IncompleteAndCancelledScansPreserveTheLastCommittedContribution()
    {
        using var fixture = new Fixture(); var store = fixture.Store; var file = new AgentSpeedHistoryFile(Path.Combine(fixture.Root, "a"), "v1");
        store.Index(UsageSource.Codex, [file], (_, _) => new([Response("r", file.Path)], true), Day.AddHours(1), default);
        var bad = store.Index(UsageSource.Codex, [file with { Signature = "partial" }], (_, _) => new([], false), Day.AddHours(1), default);
        Assert.Equal(1, bad.Unreadable); Assert.Equal(300, Assert.Single(store.Read(Date, Date)).OutputTokens);
        using var cancel = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => store.Index(UsageSource.Codex, [file with { Signature = "new" }], (_, _) =>
        { cancel.Cancel(); return new([Response("changed", file.Path, 900)], true); }, Day.AddHours(1), cancel.Token));
        Assert.Equal(300, Assert.Single(store.Read(Date, Date)).OutputTokens);
    }

    [Fact]
    public void IncompletePrefixIsRetriedAndGrowsWithoutReplacingHistoryWithSmallerUsage()
    {
        using var fixture = new Fixture(); var store = fixture.Store;
        var file = new AgentSpeedHistoryFile(Path.Combine(fixture.Root, "partial"), "v1");
        store.Index(UsageSource.Codex, [file], (_, _) => new([Response("r", file.Path)], false), Day.AddHours(1), default);
        Assert.Equal(300, Assert.Single(store.Read(Date, Date)).OutputTokens);
        var retry = store.Index(UsageSource.Codex, [file], (_, _) => new([Response("r", file.Path, 600)], false), Day.AddHours(1), default);
        Assert.Equal(1, retry.Read); Assert.Equal(600, Assert.Single(store.Read(Date, Date)).OutputTokens);
        store.Index(UsageSource.Codex, [file], (_, _) => new([Response("r", file.Path, 100)], false), Day.AddHours(1), default);
        Assert.Equal(600, Assert.Single(store.Read(Date, Date)).OutputTokens);
        store.Index(UsageSource.Codex, [file], (_, _) => new([Response("r", file.Path, 900)], true), Day.AddHours(1), default);
        Assert.Equal(900, Assert.Single(store.Read(Date, Date)).OutputTokens);
        Assert.Equal(1, store.Index(UsageSource.Codex, [file], (_, _) => throw new Exception("must reuse"), Day.AddHours(1), default).Reused);
    }

    [Fact]
    public void FutureResponseIsIndexedWhenItsTimestampArrivesWithoutAFileChange()
    {
        using var fixture = new Fixture(); var store = fixture.Store; var file = new AgentSpeedHistoryFile(Path.Combine(fixture.Root, "a"), "v1");
        AgentSpeedHistoryFileResult Read(AgentSpeedHistoryFile value, CancellationToken _) => new([Response("future", value.Path)], true);
        store.Index(UsageSource.Codex, [file], Read, Day.AddSeconds(2), default);
        Assert.Empty(store.Read(Date, Date));
        store.Index(UsageSource.Codex, [file], Read, Day.AddSeconds(5), default);
        Assert.Equal(1, Assert.Single(store.Read(Date, Date)).Requests);
    }

    [Fact]
    public void HistoryKeepsOnlyAggregatesAndHashesAndCsvUsesRecordedDuration()
    {
        using var fixture = new Fixture(); var store = fixture.Store; var file = new AgentSpeedHistoryFile(Path.Combine(fixture.Root, "private-project-name"), "1:2");
        store.Index(UsageSource.Codex, [file], (_, _) => new([Response("private-provider-response-id", file.Path)], true), Day.AddHours(1), default);
        using (var connection = new SqliteConnection("Data Source=" + store.Path + ";Pooling=False"))
        {
            connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "SELECT file_hash,key_hash FROM speed_owners";
            using var rows = command.ExecuteReader(); Assert.True(rows.Read()); Assert.Equal(64, rows.GetString(0).Length); Assert.Equal(64, rows.GetString(1).Length);
        }
        foreach (var path in Directory.GetFiles(fixture.Root, "*.sqlite3*"))
        {
            var raw = Encoding.UTF8.GetString(File.ReadAllBytes(path));
            Assert.DoesNotContain("private-project-name", raw); Assert.DoesNotContain("private-provider-response-id", raw);
        }
        var row = Assert.Single(store.Read(Date, Date));
        var csv = AgentSpeedHistoryCsv.Build([row with { Model = "=HYPERLINK(\"evil\")" }]);
        Assert.Contains("'=HYPERLINK", csv); Assert.Contains(",3000,100,400,", csv);
    }

    [Fact]
    public void CorruptHistoryIsPreservedAndRepairMakesAnExactBackup()
    {
        using var fixture = new Fixture(); var bytes = Encoding.UTF8.GetBytes("corrupt history sentinel"); File.WriteAllBytes(fixture.Store.Path, bytes);
        Assert.Throws<SqliteException>(() => fixture.Store.Read(Date, Date));
        Assert.Equal(bytes, File.ReadAllBytes(fixture.Store.Path));
        var backup = fixture.Store.BackupAndReset(default);
        Assert.Equal(bytes, File.ReadAllBytes(backup)); Assert.Empty(fixture.Store.Read(Date, Date));
    }

    [Fact]
    public void HistoricalBackfillIgnoresPreservedOldMtimeAndStoresMeasuredPeakSeparately()
    {
        using var fixture = new Fixture(); using var logs = UsageLogPaths.PushRoot(Path.Combine(fixture.Root, "logs"));
        var root = Path.Combine(UsageLogPaths.GetOverrideRoot(UsageSource.Codex)!, "archived_sessions"); Directory.CreateDirectory(root);
        var path = Path.Combine(root, "old.jsonl");
        string Line(int second, string type, object payload) => JsonSerializer.Serialize(new { timestamp = Day.AddDays(-30).AddSeconds(second), type, payload }) + "\n";
        File.WriteAllText(path, Line(0, "session_meta", new { id = "old" }) + Line(1, "event_msg", new { type = "task_started" }) +
            Line(4, "token_usage_record", new { response_id = "old-r", usage = new { input_tokens = 900, output_tokens = 300 } }));
        File.SetLastWriteTimeUtc(path, Day.AddYears(-1).UtcDateTime);
        var indexer = new AgentSpeedHistoryIndexer(fixture.Store);
        Assert.Equal(1, indexer.Index(UsageSource.Codex, Day, default).Read);
        var row = Assert.Single(fixture.Store.Read(Date.AddDays(-30), Date)); Assert.Equal(Date.AddDays(-30), row.Day); Assert.Null(row.OutputPeak);
        var engine = new AgentSpeedEngine(); engine.Add(Response("r", "a"));
        Assert.True(fixture.Store.TryRecordPeak(UsageSource.Codex, engine.Snapshot(Day.AddSeconds(4), Day)));
        Assert.Equal(1, indexer.Index(UsageSource.Codex, Day, default).Reused);
        File.Delete(path);
        Assert.Equal(300, Assert.Single(fixture.Store.Read(Date.AddDays(-30), Date)).OutputTokens);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "speed-history-tests-" + Guid.NewGuid().ToString("N"));
        public AgentSpeedHistoryStore Store { get; }
        public Fixture() { Directory.CreateDirectory(Root); Store = new(Path.Combine(Root, "history.sqlite3")); }
        public void Dispose() { Directory.Delete(Root, recursive: true); }
    }
}
