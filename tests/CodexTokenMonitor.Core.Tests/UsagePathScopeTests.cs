using System.Text.Json;
using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class UsagePathScopeTests
{
    private static readonly DateTimeOffset Day = new(2026, 8, 24, 0, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public void NestedScopesRestoreBothRoots()
    {
        using var fixture = new IsolatedPaths();
        var originalCache = MonitorCachePaths.LocalAppData;
        var originalLog = UsageLogPaths.GetOverrideRoot(UsageSource.Codex);
        using (MonitorCachePaths.PushLocalAppDataRoot(fixture.Cache("outer")))
        using (UsageLogPaths.PushRoot(fixture.Logs("outer")))
        {
            AssertRoots(fixture, "outer");
            using (MonitorCachePaths.PushLocalAppDataRoot(fixture.Cache("inner")))
            using (UsageLogPaths.PushRoot(fixture.Logs("inner")))
                AssertRoots(fixture, "inner");
            AssertRoots(fixture, "outer");
        }
        Assert.Equal(originalCache, MonitorCachePaths.LocalAppData);
        Assert.Equal(originalLog, UsageLogPaths.GetOverrideRoot(UsageSource.Codex));
    }

    [Fact]
    public void RepeatedDisposeDoesNotOverwriteANewerScope()
    {
        using var fixture = new IsolatedPaths();
        var originalCache = MonitorCachePaths.LocalAppData;
        var originalLog = UsageLogPaths.GetOverrideRoot(UsageSource.Codex);
        var cache = MonitorCachePaths.PushLocalAppDataRoot(fixture.Cache("first"));
        var logs = UsageLogPaths.PushRoot(fixture.Logs("first"));
        logs.Dispose();
        cache.Dispose();
        using (MonitorCachePaths.PushLocalAppDataRoot(fixture.Cache("second")))
        using (UsageLogPaths.PushRoot(fixture.Logs("second")))
        {
            logs.Dispose();
            cache.Dispose();
            AssertRoots(fixture, "second");
        }
        Assert.Equal(originalCache, MonitorCachePaths.LocalAppData);
        Assert.Equal(originalLog, UsageLogPaths.GetOverrideRoot(UsageSource.Codex));
    }

    [Fact]
    public async Task InheritedScopeCanBeDisposedInChildAndParentContextsIndependently()
    {
        using var fixture = new IsolatedPaths();
        var originalCache = MonitorCachePaths.LocalAppData;
        var originalLog = UsageLogPaths.GetOverrideRoot(UsageSource.Codex);
        using var cache = MonitorCachePaths.PushLocalAppDataRoot(fixture.Cache("parent"));
        using var logs = UsageLogPaths.PushRoot(fixture.Logs("parent"));
        await Task.Run(async () =>
        {
            AssertRoots(fixture, "parent");
            logs.Dispose();
            cache.Dispose();
            Assert.Equal(originalCache, MonitorCachePaths.LocalAppData);
            Assert.Equal(originalLog, UsageLogPaths.GetOverrideRoot(UsageSource.Codex));
            using (MonitorCachePaths.PushLocalAppDataRoot(fixture.Cache("child")))
            using (UsageLogPaths.PushRoot(fixture.Logs("child")))
            {
                await Task.Yield();
                AssertRoots(fixture, "child");
            }
            Assert.Equal(originalCache, MonitorCachePaths.LocalAppData);
            Assert.Equal(originalLog, UsageLogPaths.GetOverrideRoot(UsageSource.Codex));
        });
        AssertRoots(fixture, "parent");
        logs.Dispose();
        cache.Dispose();
        Assert.Equal(originalCache, MonitorCachePaths.LocalAppData);
        Assert.Equal(originalLog, UsageLogPaths.GetOverrideRoot(UsageSource.Codex));
    }

    [Fact]
    public async Task ExceptionsAfterAwaitRestoreTheCallingRoots()
    {
        using var fixture = new IsolatedPaths();
        using var cache = MonitorCachePaths.PushLocalAppDataRoot(fixture.Cache("outer"));
        using var logs = UsageLogPaths.PushRoot(fixture.Logs("outer"));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            using var nestedCache = MonitorCachePaths.PushLocalAppDataRoot(fixture.Cache("inner"));
            using var nestedLogs = UsageLogPaths.PushRoot(fixture.Logs("inner"));
            await Task.Yield();
            AssertRoots(fixture, "inner");
            throw new InvalidOperationException("controlled failure");
        });
        AssertRoots(fixture, "outer");
    }

    [Fact]
    public async Task ParallelRealReadersUseTheirOwnLogsAndCaches()
    {
        using var fixture = new IsolatedPaths();
        fixture.WriteCodexLog("a", inputTokens: 100);
        fixture.WriteCodexLog("b", inputTokens: 900);
        var originalCache = MonitorCachePaths.LocalAppData;
        var originalLog = UsageLogPaths.GetOverrideRoot(UsageSource.Codex);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readersReady = 0;
        async Task Read(string name, long inputTokens)
        {
            using var cache = MonitorCachePaths.PushLocalAppDataRoot(fixture.Cache(name));
            using var logs = UsageLogPaths.PushRoot(fixture.Logs(name));
            try
            {
                if (Interlocked.Increment(ref readersReady) == 2) ready.SetResult();
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
                for (var index = 0; index < 4; index++)
                {
                    AssertRoots(fixture, name);
                    var row = Assert.Single(CodexUsageReader.ReadTransientDetailRows(Day, Day.AddDays(1)));
                    Assert.Equal(inputTokens, row.InputTokens);
                    Assert.Equal(inputTokens + 10, row.TotalTokens);
                    var summary = CodexUsageReader.ReadRange(Day, Day.AddDays(1), includeLiveToday: false);
                    Assert.Equal(inputTokens, summary.InputTokens);
                    Assert.Equal(inputTokens + 10, summary.TotalTokens);
                    Assert.StartsWith(fixture.Cache(name), UsageCacheStore.GetCachePath("CodexTokenMonitor"));
                    await Task.Yield();
                }
            }
            finally
            {
                // The scoped root is still active: this only deletes this task's
                // generated cache, never the user's cache or the other reader's.
                UsageCacheStore.Delete("CodexTokenMonitor");
            }
        }
        await Task.WhenAll(Task.Run(() => Read("a", 100)), Task.Run(() => Read("b", 900)));
        Assert.Equal(originalCache, MonitorCachePaths.LocalAppData);
        Assert.Equal(originalLog, UsageLogPaths.GetOverrideRoot(UsageSource.Codex));
    }

    private static void AssertRoots(IsolatedPaths fixture, string name)
    {
        Assert.Equal(fixture.Cache(name), MonitorCachePaths.LocalAppData);
        foreach (var source in Enum.GetValues<UsageSource>())
            Assert.Equal(Path.Combine(fixture.Logs(name), source.ToString()), UsageLogPaths.GetOverrideRoot(source));
    }

    private sealed class IsolatedPaths : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), $"UsagePathScopeTests-{Guid.NewGuid():N}");
        public string Cache(string name) => Path.Combine(root, name, "cache");
        public string Logs(string name) => Path.Combine(root, name, "logs");

        public void WriteCodexLog(string name, long inputTokens)
        {
            var sessions = Path.Combine(Logs(name), "Codex", "sessions", "2026", "08", "24");
            Directory.CreateDirectory(sessions);
            File.WriteAllText(Path.Combine(sessions, "rollout-shared-id.jsonl"), JsonSerializer.Serialize(new
            {
                timestamp = "2026-08-24T01:00:00Z",
                type = "event_msg",
                payload = new
                {
                    type = "token_count",
                    turn_id = "same-turn-id-across-roots",
                    info = new
                    {
                        last_token_usage = new
                        {
                            input_tokens = inputTokens,
                            cached_input_tokens = 0,
                            output_tokens = 10,
                            reasoning_output_tokens = 0,
                            total_tokens = inputTokens + 10
                        }
                    }
                }
            }) + Environment.NewLine);
        }

        public void Dispose()
        {
            var resolved = Path.GetFullPath(root);
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(temp, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolved).StartsWith("UsagePathScopeTests-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing to clean up outside the isolated test directory.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        }
    }
}
