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
    private readonly bool watchFileSystem;
    private readonly Dictionary<string, DateTime> candidates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<DiscoveryChange> changes = new();
    private string[] files = [];
    private DateTimeOffset nextDiscovery;
    private DateTimeOffset nextWatcherCheck;
    private DateTimeOffset? scanDay;
    private int discoveryErrors;
    private readonly Dictionary<string, FileSystemWatcher> watchers = new(StringComparer.OrdinalIgnoreCase);
    private int queuedChanges, discoveryRequested;
    private volatile bool disposed;
    internal int FullDiscoveryCount { get; private set; }
    public UsageSource Source { get; }
    UsageSource? IAgentSpeedSampler.Source => Source;
    public event EventHandler? Changed;

    public AgentSpeedMonitor(string? home = null, UsageSource source = UsageSource.Codex, AgentSpeedEngine? sharedEngine = null,
        bool watchFileSystem = true)
    {
        Source = source; roots = AgentSpeedPaths.Roots(source, home).Select(Path.GetFullPath).ToArray(); engine = sharedEngine ?? new();
        this.watchFileSystem = watchFileSystem;
        if (source == UsageSource.ZCode) database = new(AgentSpeedPaths.ZCodePath(home));
    }

    public AgentSpeedSnapshot Read(DateTimeOffset now, DateTimeOffset dayStart, CancellationToken token = default,
        AgentSpeedMetric metric = AgentSpeedMetric.Output, AgentSpeedSnapshotOptions options = AgentSpeedSnapshotOptions.Full)
    {
        var errors = ReadChanges(now, dayStart, token);
        return engine.Snapshot(now, dayStart, metric, Source, options) with { UnreadableFiles = errors };
    }

    internal int ReadChanges(DateTimeOffset now, DateTimeOffset dayStart, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        token.ThrowIfCancellationRequested();
        var unreadable = 0;
        if (database is not null) unreadable += database.Read(engine, dayStart, now, token);
        else
        {
            MaintainFiles(now, dayStart, token);
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

    private void MaintainFiles(DateTimeOffset now, DateTimeOffset dayStart, CancellationToken token)
    {
        var requested = Interlocked.Exchange(ref discoveryRequested, 0) != 0;
        var full = scanDay != dayStart || now >= nextDiscovery || requested;
        var changed = false;
        // Checking whether a root exists is cheap. Only roots without a functioning watcher
        // need the short polling fallback; a missing archive root must not rescan live sessions.
        if (full || now >= nextWatcherCheck)
        {
            foreach (var root in roots)
            {
                token.ThrowIfCancellationRequested();
                if (!Directory.Exists(root)) StopWatching(root);
                var wasWatching = watchers.ContainsKey(root);
                var watched = wasWatching || Watch(root);
                if (!full && (!watched || !wasWatching)) changed |= RefreshSubtree(root, token);
            }
            nextWatcherCheck = now.AddSeconds(5);
        }
        if (full)
        {
            // Build separately: cancellation cannot replace a complete catalog with a prefix.
            var discovered = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            discoveryErrors = 0;
            foreach (var root in roots)
            {
                token.ThrowIfCancellationRequested();
                if (Discover(root, discovered, token)) continue;
                foreach (var pair in candidates.Where(pair => IsWithin(pair.Key, root))) discovered[pair.Key] = pair.Value;
            }
            token.ThrowIfCancellationRequested();
            candidates.Clear();
            foreach (var pair in discovered) candidates.Add(pair.Key, pair.Value);
            scanDay = dayStart; nextDiscovery = now.AddMinutes(5); FullDiscoveryCount++; changed = true;
            tail.PruneBeforeUtc(dayStart.AddDays(-1).UtcDateTime);
        }
        // Watcher callbacks only enqueue; parsers, readers and the catalog belong to this worker.
        // Leave an interrupted item at the queue head so cancellation never drops a rename/create.
        var count = Volatile.Read(ref queuedChanges);
        for (var index = 0; index < count && changes.TryPeek(out var change); index++)
        {
            token.ThrowIfCancellationRequested();
            if (change.Kind == WatcherChangeTypes.All)
            {
                StopWatching(change.Path); Watch(change.Path);
                changed |= RefreshSubtree(change.Path, token);
            }
            else
            {
                if (change.OldPath is not null) changed |= RemoveSubtree(change.OldPath);
                if (change.Kind == WatcherChangeTypes.Deleted) changed |= RemoveSubtree(change.Path);
                else changed |= RefreshPath(change.Path, change.Kind != WatcherChangeTypes.Changed, token);
            }
            changes.TryDequeue(out _); Interlocked.Decrement(ref queuedChanges);
        }
        if (!changed) return;
        IEnumerable<string> selected = candidates.Keys;
        // Migration leaves older generations on disk. Select before applying the live mtime
        // cutoff, just as native DSH discovery does, so an old generation cannot reappear.
        if (Source == UsageSource.Dsh)
            selected = selected.GroupBy(path => Path.GetDirectoryName(path)!, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(path => DshGeneration(Path.GetFileName(path))).ThenBy(path => path, StringComparer.OrdinalIgnoreCase).First());
        selected = selected.Where(path => candidates[path] >= dayStart.AddDays(-1).UtcDateTime);
        files = (Source == UsageSource.Qoder ? selected.Order(StringComparer.OrdinalIgnoreCase)
            : selected.OrderBy(path => candidates[path]).ThenBy(path => path, StringComparer.Ordinal)).ToArray();
        var present = files.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var stale in parsers.Keys.Where(path => !present.Contains(path)).ToArray())
        {
            var agentId = parsers[stale].Agent.Id;
            parsers.Remove(stale); if (compressed.Remove(stale, out var reader)) reader.Dispose(); opened.Remove(stale);
            if (!parsers.Values.Any(parser => parser.Agent.Id == agentId)) engine.RemoveAgent(agentId);
        }
    }

    private bool Discover(string directory, Dictionary<string, DateTime> result, CancellationToken token)
    {
        try
        {
            if (!Directory.Exists(directory)) return true;
            if (!SafePath(directory)) return true;
            foreach (var path in Directory.EnumerateFiles(directory, Source == UsageSource.Kimi ? "wire.jsonl" : "*.jsonl*", new EnumerationOptions
                { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint,
                    MaxRecursionDepth = Source == UsageSource.Dsh ? int.MaxValue : Math.Max(0, 12 - RelativeDepth(directory)) }))
            {
                token.ThrowIfCancellationRequested();
                if (!CandidateName(path)) continue;
                try { result[path] = File.GetLastWriteTimeUtc(path); }
                catch (IOException) { discoveryErrors++; }
                catch (UnauthorizedAccessException) { discoveryErrors++; }
            }
            return true;
        }
        catch (IOException) { discoveryErrors++; return false; }
        catch (UnauthorizedAccessException) { discoveryErrors++; return false; }
    }

    private bool RefreshSubtree(string directory, CancellationToken token)
    {
        var discovered = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        if (!Discover(directory, discovered, token)) return false;
        token.ThrowIfCancellationRequested();
        var changed = RemoveSubtree(directory);
        foreach (var pair in discovered) { candidates[pair.Key] = pair.Value; changed = true; }
        return changed;
    }

    private bool RefreshPath(string path, bool includeDirectory, CancellationToken token)
    {
        if (!roots.Any(root => IsWithin(path, root))) return false;
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) != 0)
                return includeDirectory && RefreshSubtree(path, token);
            if (!CandidateName(path) || !SafePath(path)) return RemoveSubtree(path);
            candidates[path] = File.GetLastWriteTimeUtc(path); return true;
        }
        catch (FileNotFoundException) { return RemoveSubtree(path); }
        catch (DirectoryNotFoundException) { return RemoveSubtree(path); }
        catch (IOException) { discoveryErrors++; return false; }
        catch (UnauthorizedAccessException) { discoveryErrors++; return false; }
    }

    private bool RemoveSubtree(string path)
    {
        var removed = false;
        foreach (var file in candidates.Keys.Where(file => IsWithin(file, path)).ToArray()) removed |= candidates.Remove(file);
        return removed;
    }

    private bool CandidateName(string path) => Source == UsageSource.Dsh ? DshGeneration(Path.GetFileName(path)) >= 0
        : Source == UsageSource.Kimi ? Path.GetFileName(path).Equals("wire.jsonl", StringComparison.OrdinalIgnoreCase)
        : path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) || Source == UsageSource.Codex && path.EndsWith(".jsonl.zst", StringComparison.OrdinalIgnoreCase);

    private static int DshGeneration(string name)
    {
        if (!name.StartsWith("session", StringComparison.OrdinalIgnoreCase)) return -1;
        var suffix = name.EndsWith(".jsonl.zstd", StringComparison.OrdinalIgnoreCase) ? 11 : name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) ? 6 : 0;
        if (suffix == 0) return -1;
        var version = name[7..^suffix];
        if (version.StartsWith('.')) version = version[1..];
        if (version.Length == 0) return 0;
        return version.Length > 1 && version[0] is 'v' or 'V' && int.TryParse(version.AsSpan(1), out var value) && value >= 0 ? value : -1;
    }

    private int RelativeDepth(string path)
    {
        var root = roots.First(root => IsWithin(path, root));
        var relative = Path.GetRelativePath(root, path);
        return relative == "." ? 0 : relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length;
    }

    private bool SafePath(string path)
    {
        var root = roots.FirstOrDefault(root => IsWithin(path, root));
        if (root is null || Source != UsageSource.Dsh && RelativeDepth(Path.GetDirectoryName(path) is { } parent && !Directory.Exists(path) ? parent : path) > 12) return false;
        for (var current = path; current is not null && IsWithin(current, root); current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
        return true;
    }

    private static bool IsWithin(string path, string directory) => path.Equals(directory, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private bool Watch(string root)
    {
        if (!watchFileSystem || disposed || !Directory.Exists(root)) return false;
        try
        {
            if (!SafePath(root)) return false;
            var watcher = new FileSystemWatcher(root) { IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite };
            watcher.Changed += (_, e) => QueueChange(new(e.ChangeType, e.FullPath));
            watcher.Created += (_, e) => QueueChange(new(e.ChangeType, e.FullPath));
            watcher.Deleted += (_, e) => QueueChange(new(e.ChangeType, e.FullPath));
            watcher.Renamed += (_, e) => QueueChange(new(e.ChangeType, e.FullPath, e.OldFullPath));
            watcher.Error += (_, _) => QueueChange(new(WatcherChangeTypes.All, root));
            watcher.EnableRaisingEvents = true; watchers[root] = watcher;
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private void StopWatching(string root)
    {
        if (watchers.Remove(root, out var watcher)) watcher.Dispose();
    }

    private void QueueChange(DiscoveryChange change)
    {
        if (disposed) return;
        if (Interlocked.Increment(ref queuedChanges) <= 2048) changes.Enqueue(change);
        else { Interlocked.Decrement(ref queuedChanges); Interlocked.Exchange(ref discoveryRequested, 1); }
        if (disposed) return;
        try { Changed?.Invoke(this, EventArgs.Empty); }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { } // A subscriber may be shutting its Dispatcher down.
    }

    internal void NotifyWatcherError(string root) => QueueChange(new(WatcherChangeTypes.All, Path.GetFullPath(root)));
    private sealed record DiscoveryChange(WatcherChangeTypes Kind, string Path, string? OldPath = null);
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
    public AgentSpeedSnapshot Read(DateTimeOffset now, DateTimeOffset dayStart, CancellationToken token = default, AgentSpeedMetric metric = AgentSpeedMetric.Output,
        AgentSpeedSnapshotOptions options = AgentSpeedSnapshotOptions.Full)
    {
        var errors = readers.Sum(reader => reader.ReadChanges(now, dayStart, token));
        return engine.Snapshot(now, dayStart, metric, options: options) with { UnreadableFiles = errors };
    }
    public AgentSpeedSnapshot SourceSnapshot(UsageSource source, DateTimeOffset now, DateTimeOffset dayStart, AgentSpeedMetric metric,
        AgentSpeedSnapshotOptions options = AgentSpeedSnapshotOptions.Full) => engine.Snapshot(now, dayStart, metric, source, options);
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
