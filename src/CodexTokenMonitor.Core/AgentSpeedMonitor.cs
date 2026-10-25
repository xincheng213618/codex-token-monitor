namespace CodexTokenMonitor;

/// <summary>Read-only native sampling, independent of the usage-cache gate. A worker calls it sequentially.</summary>
internal sealed class AgentSpeedMonitor : IAgentSpeedSampler
{
    private readonly string[] roots;
    private readonly LiveFileTailReader tail = new();
    private readonly Dictionary<string, IAgentSpeedParser> parsers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ZstdJsonlTailReader> compressed = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> opened = new(StringComparer.OrdinalIgnoreCase);
    private readonly AgentSpeedEngine engine;
    private readonly ZCodeSpeedDatabase? database;
    private string[] files = [];
    private DateTimeOffset nextDiscovery;
    private DateTimeOffset? scanDay;
    private int discoveryErrors;
    private readonly Dictionary<string, FileSystemWatcher> watchers = new(StringComparer.OrdinalIgnoreCase);
    private int dirty;
    private bool disposed;
    public UsageSource Source { get; }
    UsageSource? IAgentSpeedSampler.Source => Source;
    public event EventHandler? Changed;

    public AgentSpeedMonitor(string? home = null, UsageSource source = UsageSource.Codex, AgentSpeedEngine? sharedEngine = null)
    {
        Source = source; roots = AgentSpeedPaths.Roots(source, home); engine = sharedEngine ?? new();
        if (source == UsageSource.ZCode) database = new(AgentSpeedPaths.ZCodePath(home));
    }

    public AgentSpeedSnapshot Read(DateTimeOffset now, DateTimeOffset dayStart, CancellationToken token = default,
        AgentSpeedMetric metric = AgentSpeedMetric.Output)
    {
        var errors = ReadChanges(now, dayStart, token);
        return engine.Snapshot(now, dayStart, metric, Source) with { UnreadableFiles = errors };
    }

    internal int ReadChanges(DateTimeOffset now, DateTimeOffset dayStart, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        token.ThrowIfCancellationRequested();
        var unreadable = 0;
        if (scanDay != dayStart) { scanDay = dayStart; nextDiscovery = default; }
        if (database is not null) unreadable += database.Read(engine, dayStart, now, token);
        else
        {
            if (now >= nextDiscovery || Interlocked.Exchange(ref dirty, 0) != 0)
            {
                var discovered = new List<string>(); discoveryErrors = 0;
                foreach (var root in roots)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        if (!Directory.Exists(root)) continue;
                        Watch(root);
                        foreach (var file in AgentSpeedPaths.Files(Source, root))
                        {
                            token.ThrowIfCancellationRequested();
                            try { if (File.GetLastWriteTimeUtc(file) >= dayStart.AddDays(-1).UtcDateTime) discovered.Add(file); }
                            catch (IOException) { discoveryErrors++; }
                            catch (UnauthorizedAccessException) { discoveryErrors++; }
                        }
                    }
                    catch (IOException) { discoveryErrors++; }
                    catch (UnauthorizedAccessException) { discoveryErrors++; }
                }
                files = AgentSpeedPaths.StartupOrder(Source, discovered.Distinct(StringComparer.OrdinalIgnoreCase)).ToArray();
                var present = files.ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var stale in parsers.Keys.Where(path => !present.Contains(path)).ToArray())
                {
                    var agentId = parsers[stale].Agent.Id;
                    parsers.Remove(stale); if (compressed.Remove(stale, out var oldReader)) oldReader.Dispose(); opened.Remove(stale);
                    if (!parsers.Values.Any(parser => parser.Agent.Id == agentId)) engine.RemoveAgent(agentId);
                }
                tail.PruneBeforeUtc(dayStart.AddDays(-1).UtcDateTime);
                nextDiscovery = now.AddSeconds(5);
            }
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                if (!parsers.TryGetValue(file, out var parser))
                {
                    var agentId = Source == UsageSource.Qoder ? Path.GetDirectoryName(Path.GetDirectoryName(file))! : file;
                    parser = Source == UsageSource.Qoder ? parsers.Values.FirstOrDefault(p => p.Agent.Id == agentId) : null;
                    parsers[file] = parser ??= AgentSpeedPaths.Parser(Source, agentId, key => engine.CanAccept(Source, key, agentId));
                }
                void Consume(string line)
                {
                    token.ThrowIfCancellationRequested();
                    foreach (var response in parser.ReadResponses(line)) engine.Add(response);
                }
                // The first read of a later Qoder segment belongs to the same session parser.
                void Restart() { if (opened.Contains(file) || Source != UsageSource.Qoder) parser.Reset(); opened.Add(file); }
                if (file.EndsWith(".zst", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".zstd", StringComparison.OrdinalIgnoreCase))
                {
                    if (!compressed.TryGetValue(file, out var reader)) compressed[file] = reader = new();
                    if (!reader.Read(file, Consume, Restart, token)) unreadable++;
                }
                else if (!tail.ReadNewLinesWhile(file, dayStart, line => { Consume(line); return true; }, token, Restart)) unreadable++;
                engine.Update(parser.Agent);
            }
        }
        return unreadable + discoveryErrors;
    }

    private void Watch(string root)
    {
        if (watchers.ContainsKey(root)) return;
        try
        {
            var watcher = new FileSystemWatcher(root) { IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite };
            watcher.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
            void Rediscover() { Interlocked.Exchange(ref dirty, 1); Changed?.Invoke(this, EventArgs.Empty); }
            watcher.Created += (_, _) => Rediscover();
            watcher.Deleted += (_, _) => Rediscover();
            watcher.Renamed += (_, _) => Rediscover();
            watcher.Error += (_, _) => Rediscover();
            watcher.EnableRaisingEvents = true; watchers[root] = watcher;
        }
        catch (IOException) { } // Five-second discovery remains the fallback when watching is unavailable.
        catch (UnauthorizedAccessException) { }
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        foreach (var watcher in watchers.Values) watcher.Dispose();
        foreach (var reader in compressed.Values) reader.Dispose();
        watchers.Clear(); compressed.Clear();
    }
}

internal sealed class AgentSpeedHub : IAgentSpeedSampler
{
    private readonly AgentSpeedEngine engine = new();
    private readonly AgentSpeedMonitor[] readers;
    public UsageSource? Source => null;
    public event EventHandler? Changed;
    public AgentSpeedHub()
    {
        readers = UsageSourceRegistry.All.Select(source => new AgentSpeedMonitor(source: source.Source, sharedEngine: engine)).ToArray();
        foreach (var reader in readers) reader.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }
    public AgentSpeedSnapshot Read(DateTimeOffset now, DateTimeOffset dayStart, CancellationToken token = default, AgentSpeedMetric metric = AgentSpeedMetric.Output)
    {
        var errors = readers.Sum(reader => reader.ReadChanges(now, dayStart, token));
        return engine.Snapshot(now, dayStart, metric) with { UnreadableFiles = errors };
    }
    public AgentSpeedSnapshot SourceSnapshot(UsageSource source, DateTimeOffset now, DateTimeOffset dayStart, AgentSpeedMetric metric) => engine.Snapshot(now, dayStart, metric, source);
    public void Dispose() { foreach (var reader in readers) reader.Dispose(); }
}

internal static class AgentSpeedPaths
{
    public static string[] Roots(UsageSource source, string? home = null)
    {
        var isolated = home ?? UsageLogPaths.GetOverrideRoot(source);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string Config(string variable, string fallback) => Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value ? value : Path.Combine(profile, fallback);
        return source switch
        {
            UsageSource.Codex => [Path.Combine(isolated ?? Config("CODEX_HOME", ".codex"), "sessions"), Path.Combine(isolated ?? Config("CODEX_HOME", ".codex"), "archived_sessions")],
            UsageSource.ClaudeCode => [isolated ?? Path.Combine(Config("CLAUDE_CONFIG_DIR", ".claude"), "projects")],
            UsageSource.Dsh => [isolated ?? Path.Combine(Config("DSH_HOME", ".dsh"), "sessions")],
            UsageSource.WorkBuddy => isolated is not null ? [isolated] : [Path.Combine(profile, ".workbuddy", "projects"), Path.Combine(profile, ".workbuddy-ai", "projects")],
            UsageSource.Kimi => isolated is not null ? [isolated] : [Path.Combine(Config("KIMI_CODE_HOME", ".kimi-code"), "sessions"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "kimi-desktop", "daimon-share", "daimon", "runtime", "kimi-code", "home", "sessions")],
            UsageSource.Qoder => [isolated ?? Path.Combine(profile, ".qoder", "logs", "sessions")],
            UsageSource.ZCode => [],
            _ => throw new ArgumentOutOfRangeException(nameof(source))
        };
    }
    public static string ZCodePath(string? home = null)
    {
        var root = home ?? UsageLogPaths.GetOverrideRoot(UsageSource.ZCode);
        if (root is not null) return Path.Combine(root, "db", "db.sqlite");
        var baseDirectory = Environment.GetEnvironmentVariable("ZCODE_DATA_BASE_DIR");
        return string.IsNullOrWhiteSpace(baseDirectory) ? ZCodeCliUsageDatabase.DefaultPath : Path.Combine(baseDirectory, ".zcode", "cli", "db", "db.sqlite");
    }
    public static IEnumerable<string> Files(UsageSource source, string root)
    {
        if (source == UsageSource.Dsh) return DshUsageReader.SelectHighestGenerations(root);
        return Directory.EnumerateFiles(root, source == UsageSource.Kimi ? "wire.jsonl" : "*.jsonl*", new EnumerationOptions
        { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint, MaxRecursionDepth = 12 })
            .Where(path => path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) || source == UsageSource.Codex && path.EndsWith(".jsonl.zst", StringComparison.OrdinalIgnoreCase));
    }
    public static IEnumerable<string> StartupOrder(UsageSource source, IEnumerable<string> files) => source == UsageSource.Qoder
        ? files.Order(StringComparer.OrdinalIgnoreCase) : files.OrderBy(File.GetLastWriteTimeUtc).ThenBy(path => path, StringComparer.Ordinal);
    public static IAgentSpeedParser Parser(UsageSource source, string id, Func<string, bool>? canUse = null) => source switch
    {
        UsageSource.Codex => new CodexSpeedParser(id, canUse), UsageSource.ClaudeCode => new ClaudeSpeedParser(id, canUse),
        UsageSource.Dsh => new DshSpeedParser(id, canUse), UsageSource.WorkBuddy => new WorkBuddySpeedParser(id, canUse),
        UsageSource.Kimi => new KimiSpeedParser(id), UsageSource.Qoder => new QoderSpeedParser(id),
        _ => throw new ArgumentOutOfRangeException(nameof(source))
    };
}
