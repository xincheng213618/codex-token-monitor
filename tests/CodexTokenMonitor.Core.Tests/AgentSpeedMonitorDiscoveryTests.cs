using System.Text.Json;
using CodexTokenMonitor;
using Xunit;

namespace CodexTokenMonitor.Core.Tests;

public sealed class AgentSpeedMonitorDiscoveryTests
{
    private static readonly DateTimeOffset Day = new(2026, 10, 26, 0, 0, 0, TimeSpan.FromHours(8));
    private static DateTimeOffset At(double seconds) => Day.AddSeconds(seconds);
    private static string Line(double seconds, string type, object payload) => JsonSerializer.Serialize(new { timestamp = At(seconds), type, payload }) + "\n";
    private static string Response(string key) => Line(1, "event_msg", new { type = "task_started" }) +
        Line(4, "token_usage_record", new { response_id = key, usage = new { input_tokens = 100, output_tokens = 300 } });
    private static void Write(string file, string key)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, Line(0, "session_meta", new { id = key }) + Response(key));
    }
    private static void Eventually(AgentSpeedMonitor monitor, int requests, double now = 10) =>
        Assert.True(SpinWait.SpinUntil(() => monitor.Read(At(now), Day).Requests == requests, TimeSpan.FromSeconds(4)),
            $"Expected {requests} requests after the file notification.");

    [Fact]
    public void WatcherFindsNewAndPreviouslyOldLogsWithoutPeriodicFullScans()
    {
        using var home = new Home();
        var old = Path.Combine(home.Sessions, "old.jsonl"); Write(old, "old");
        File.SetLastWriteTimeUtc(old, Day.AddDays(-3).UtcDateTime);
        using var monitor = new AgentSpeedMonitor(home.Root);
        Assert.Equal(0, monitor.Read(At(5), Day).Requests);
        Assert.Equal(1, monitor.FullDiscoveryCount);
        for (var second = 6; second < 100; second++) monitor.Read(At(second), Day, options: AgentSpeedSnapshotOptions.Live);
        Assert.Equal(1, monitor.FullDiscoveryCount);
        Write(Path.Combine(home.Sessions, "new.jsonl"), "new");
        Eventually(monitor, 1, 100);
        File.AppendAllText(old, Response("old-extra"));
        Eventually(monitor, 3, 101); // The older prefix is now eligible too.
        Assert.Equal(1, monitor.FullDiscoveryCount);
    }

    [Fact]
    public void MovingPopulatedDirectoriesAndArchivingKeepsDiscoveryAndResponseOwnership()
    {
        using var home = new Home();
        using var monitor = new AgentSpeedMonitor(home.Root);
        Assert.Equal(0, monitor.Read(At(5), Day).Requests);
        var incoming = Path.Combine(home.Root, "incoming");
        Write(Path.Combine(incoming, "a.jsonl"), "a"); Write(Path.Combine(incoming, "nested", "b.jsonl"), "b");
        var destination = Path.Combine(home.Sessions, "moved"); Directory.Move(incoming, destination);
        Eventually(monitor, 2);
        Directory.Move(destination, Path.Combine(home.Archive, "moved"));
        Write(Path.Combine(home.Archive, "moved", "nested", "c.jsonl"), "c");
        Eventually(monitor, 3, 11);
        Assert.Equal(1, monitor.FullDiscoveryCount);
        Directory.Delete(Path.Combine(home.Archive, "moved"), true);
        Assert.True(SpinWait.SpinUntil(() => monitor.Read(At(12), Day).Agents.Count == 0, TimeSpan.FromSeconds(4)));
        Assert.Equal(3, monitor.Read(At(13), Day).Requests); // Deleting logs retains already observed usage.
    }

    [Fact]
    public void WatcherFailureAndHealthSweepRecoverMissedDiscoveries()
    {
        using var home = new Home();
        using var monitor = new AgentSpeedMonitor(home.Root, watchFileSystem: false);
        Assert.Equal(0, monitor.Read(At(5), Day).Requests);
        Write(Path.Combine(home.Sessions, "missed.jsonl"), "missed");
        Assert.Equal(0, monitor.Read(At(6), Day).Requests);
        Assert.Equal(1, monitor.Read(At(10), Day).Requests); // Five-second fallback when watching is unavailable.
        Write(Path.Combine(home.Sessions, "error.jsonl"), "error");
        monitor.NotifyWatcherError(home.Sessions);
        Assert.Equal(2, monitor.Read(At(11), Day).Requests);
        Assert.Equal(1, monitor.FullDiscoveryCount);
        monitor.Read(At(305), Day);
        Assert.Equal(2, monitor.FullDiscoveryCount); // Periodic reconciliation remains available.
        monitor.Read(Day.AddDays(1), Day.AddDays(1));
        Assert.Equal(3, monitor.FullDiscoveryCount);
    }

    [Fact]
    public void HealthSweepFindsFilesEvenWhenAHealthyWatcherSilentlyMissesNotifications()
    {
        using var home = new Home();
        using var monitor = new AgentSpeedMonitor(home.Root);
        monitor.Read(At(5), Day);
        var watchers = (Dictionary<string, FileSystemWatcher>)typeof(AgentSpeedMonitor)
            .GetField("watchers", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(monitor)!;
        foreach (var watcher in watchers.Values) watcher.EnableRaisingEvents = false;
        Write(Path.Combine(home.Sessions, "lost-notification.jsonl"), "lost");
        Assert.Equal(0, monitor.Read(At(20), Day).Requests);
        Assert.Equal(0, monitor.Read(At(304), Day).Requests);
        Assert.Equal(1, monitor.Read(At(305), Day).Requests);
        Assert.Equal(2, monitor.FullDiscoveryCount);
    }

    [Fact]
    public void NewQoderSegmentCompletesARequestStartedInThePreviousSegment()
    {
        using var home = new Home();
        var segments = Path.Combine(home.Root, "qoder", "s", "segments"); Directory.CreateDirectory(segments);
        File.WriteAllText(Path.Combine(segments, "001.jsonl"), JsonSerializer.Serialize(new
            { ts = At(0), type = "model.request.started", request_id = "r", data = new { model = "qoder" } }) + "\n");
        using var monitor = new AgentSpeedMonitor(Path.Combine(home.Root, "qoder"), UsageSource.Qoder);
        Assert.Equal(0, monitor.Read(At(2), Day).Requests);
        File.WriteAllText(Path.Combine(segments, "002.jsonl"), JsonSerializer.Serialize(new
            { ts = At(5), type = "model.response.completed", request_id = "r", data = new { model = "qoder", output_tokens = 500, input_tokens = 1000 } }) + "\n");
        Eventually(monitor, 1);
        Assert.Equal(100, monitor.Read(At(10), Day).Average);
        Assert.Equal(1, monitor.FullDiscoveryCount);
    }

    [Fact]
    public void RootsCreatedAfterStartupAreFoundWithoutRescanningTheHealthyRoot()
    {
        using var home = new Home(createArchive: false);
        using var monitor = new AgentSpeedMonitor(home.Root);
        monitor.Read(At(5), Day);
        Write(Path.Combine(home.Archive, "late.jsonl"), "late");
        Assert.Equal(1, monitor.Read(At(10), Day).Requests);
        Assert.Equal(1, monitor.FullDiscoveryCount);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => monitor.Read(At(11), Day, canceled.Token));
        Assert.Equal(1, monitor.Read(At(12), Day).Requests);
    }

    [Fact]
    public void DshSelectsHighestGenerationBeforeApplyingTheLiveTimeCutoff()
    {
        using var home = new Home();
        var directory = Path.Combine(home.Root, "dsh", "session"); Directory.CreateDirectory(directory);
        string Dsh(string id) => JsonSerializer.Serialize(new { type = "session", id = "s", createdAt = At(0).ToUnixTimeMilliseconds() }) + "\n" +
            JsonSerializer.Serialize(new { type = "step/start", time = At(1).ToUnixTimeMilliseconds() }) + "\n" +
            JsonSerializer.Serialize(new { type = "assistant/message", time = At(4).ToUnixTimeMilliseconds(), data = new { message = new { id }, usage = new { outputTokens = 300 } } }) + "\n";
        File.WriteAllText(Path.Combine(directory, "session.v3.jsonl"), Dsh("obsolete"));
        var latest = Path.Combine(directory, "session.v4.jsonl"); File.WriteAllText(latest, Dsh("old-latest"));
        File.SetLastWriteTimeUtc(latest, Day.AddDays(-3).UtcDateTime);
        using var monitor = new AgentSpeedMonitor(Path.GetDirectoryName(directory), UsageSource.Dsh);
        Assert.Equal(0, monitor.Read(At(5), Day).Requests);
        File.SetLastWriteTimeUtc(latest, DateTime.UtcNow);
        Eventually(monitor, 1);
        Assert.Equal(1, monitor.FullDiscoveryCount);
    }

    private sealed class Home : IDisposable
    {
        public string Root { get; } = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "agent-speed-discovery-" + Guid.NewGuid().ToString("N")));
        public string Sessions => Path.Combine(Root, "sessions");
        public string Archive => Path.Combine(Root, "archived_sessions");
        public Home(bool createArchive = true) { Directory.CreateDirectory(Sessions); if (createArchive) Directory.CreateDirectory(Archive); }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
    }
}
