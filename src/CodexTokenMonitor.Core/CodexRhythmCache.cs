using System.Security.Cryptography;
using System.Text;

namespace CodexTokenMonitor;

internal sealed class CodexRhythmCache
{
    internal const string DatabaseSource = "Codex 本机会话库";
    internal const string LogSource = "Codex 本机会话日志";

    private readonly string path;
    private readonly string mutexName;

    public CodexRhythmCache(string home, string cacheFolder = "CodexTokenMonitor")
    {
        var key = HashPath(home);
        path = Path.Combine(MonitorCachePaths.LocalAppData, cacheFolder, "rhythm", $"history-v1-{key}.json");
        mutexName = $"{cacheFolder}.Rhythm.{HashPath(path)}";
    }

    public CodexRhythmSnapshot Load()
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            return JsonSerializer.Deserialize<CodexRhythmSnapshot>(stream) ?? new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    public CodexMessageHistory Update(Action<CodexRhythmSnapshot> refresh, CancellationToken token)
    {
        using var mutex = new Mutex(false, mutexName);
        try
        {
            while (!mutex.WaitOne(100)) token.ThrowIfCancellationRequested();
        }
        catch (AbandonedMutexException)
        {
            // The previous process exited mid-refresh. The saved snapshot is atomically replaced.
        }

        try
        {
            token.ThrowIfCancellationRequested();
            var snapshot = Load();
            refresh(snapshot);
            var history = snapshot.ToHistory();
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using (var stream = File.Create(temporary)) JsonSerializer.Serialize(stream, snapshot);
                token.ThrowIfCancellationRequested();
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                try { File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            return history;
        }
        finally { mutex.ReleaseMutex(); }
    }

    private static string HashPath(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (OperatingSystem.IsWindows()) full = full.ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)));
    }
}

internal sealed class CodexRhythmSnapshot
{
    public DateTimeOffset UpdatedAt { get; set; }
    public string Source { get; set; } = CodexRhythmCache.LogSource;
    public int LogReadFailures { get; set; }
    public Dictionary<string, CodexRhythmDatabaseEntry> Database { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, CodexRhythmLogEntry> Logs { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public CodexMessageHistory ToHistory()
    {
        var logs = Logs.Values.Where(entry => entry.ThreadId is not null)
            .ToLookup(entry => entry.ThreadId!, StringComparer.Ordinal);
        var databaseIsPrimary = Source == CodexRhythmCache.DatabaseSource;
        var database = Database.Where(entry => databaseIsPrimary || !logs.Contains(entry.Key))
            .Select(entry => entry.Value.Thread);
        var logThreads = logs.Where(group => !databaseIsPrimary || !Database.ContainsKey(group.Key));
        var logMessages = logThreads.SelectMany(group => group.SelectMany(entry => entry.History.Messages)
            .DistinctBy(message => message.Stamp));
        var clientIds = new HashSet<string>(StringComparer.Ordinal);
        var messages = database.SelectMany(entry => entry.Messages).Concat(logMessages)
            .Where(message => message.Stamp <= UpdatedAt).OrderBy(message => message.Stamp)
            .Where(message => string.IsNullOrEmpty(message.ClientId) || clientIds.Add(message.ClientId))
            .Select(message => message.Stamp).ToArray();
        var starts = database.SelectMany(entry => entry.TurnStarts)
            .Concat(logThreads.SelectMany(group => group.SelectMany(entry => entry.History.TurnStarts)))
            .Where(stamp => stamp <= UpdatedAt).Distinct().Order().ToArray();
        var skipped = database.Sum(entry => entry.Skipped) + (databaseIsPrimary ? 0 : LogReadFailures);
        return new(messages, starts, UpdatedAt, Source, skipped);
    }
}

internal sealed record CodexRhythmMessage(DateTimeOffset Stamp, string? ClientId = null);
internal sealed record CodexRhythmThread(CodexRhythmMessage[] Messages, DateTimeOffset[] TurnStarts, int Skipped = 0);
internal sealed record CodexRhythmProjection(long ByteOffset, long Ordinal);
internal sealed record CodexRhythmThreadMetadata(string? Source, long Created);
internal sealed record CodexRhythmDatabaseEntry(
    long DatabaseCreatedTicks, CodexRhythmThreadMetadata? Metadata, CodexRhythmProjection? Projection, CodexRhythmThread Thread);
internal sealed record CodexRhythmLogEntry(CodexLogFileIndex.Signature Signature, string? ThreadId, CodexRhythmThread History);
