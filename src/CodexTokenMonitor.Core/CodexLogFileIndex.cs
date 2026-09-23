using System.Security.Cryptography;

namespace CodexTokenMonitor;

/// <summary>
/// A rebuildable index of token-record time ranges, shared by historical usage
/// and quota scans. File dates are never treated as dates of their contents.
/// Only an unchanged, fully inspected file outside the requested range is skipped.
/// </summary>
internal sealed class CodexLogFileIndex : IDisposable
{
    internal const string FileName = "codex-log-file-index-v1.json";
    private readonly string path;
    private readonly Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
    private bool dirty;

    public CodexLogFileIndex()
    {
        path = Path.Combine(MonitorCachePaths.LocalAppData, "CodexTokenMonitor", FileName);
        try
        {
            var saved = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path));
            if (saved is not null)
                foreach (var (file, entry) in saved)
                    if (entry is not null && entry.Signature is not null &&
                        entry.First.HasValue == entry.Last.HasValue &&
                        (entry.First is null || entry.First <= entry.Last))
                        entries[file] = entry;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // An absent or damaged optimization index falls back to source logs.
        }
    }

    public bool Read(string file, DateTimeOffset start, DateTimeOffset end,
        Action<string> consumeLine, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            using var stream = Open(file);
            var before = Identify(file, stream);
            if (entries.TryGetValue(file, out var entry) && entry.Signature == before &&
                (entry.First is null || entry.Last < start || entry.First >= end))
                return true;

            stream.Position = 0;
            using var reader = new StreamReader(stream);
            DateTimeOffset? first = null, last = null;
            while (reader.ReadLine() is { } line)
            {
                token.ThrowIfCancellationRequested();
                // Include replayed records as well: a wider range is safe and
                // leaves model/replay attribution entirely with the normal parser.
                if (line.Contains("\"token_count\"", StringComparison.Ordinal) &&
                    SubagentReplayFilter.TryReadRecordTimestamp(line) is { } timestamp)
                {
                    if (first is null || timestamp < first) first = timestamp;
                    if (last is null || timestamp > last) last = timestamp;
                }
                consumeLine(line);
            }
            token.ThrowIfCancellationRequested();
            using var verification = Open(file);
            if (before != Identify(file, verification))
                return false; // Do not seal a day or index a file that changed mid-read.

            var updated = new Entry(before, first, last);
            if (entry != updated)
            {
                entries[file] = updated;
                dirty = true;
            }
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static FileStream Open(string file) =>
        new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    private static Signature Identify(string file, FileStream stream)
    {
        var info = new FileInfo(file);
        var length = stream.Length;
        // Small boundary samples also catch common same-size replacements with
        // preserved timestamps, without re-reading multi-gigabyte old sessions.
        var sample = new byte[(int)Math.Min(length, 8192)];
        var head = Math.Min(sample.Length, 4096);
        stream.Position = 0;
        stream.ReadExactly(sample.AsSpan(0, head));
        if (sample.Length > head)
        {
            stream.Position = length - (sample.Length - head);
            stream.ReadExactly(sample.AsSpan(head));
        }
        return new(length, info.CreationTimeUtc.Ticks, info.LastWriteTimeUtc.Ticks,
            Convert.ToHexString(SHA256.HashData(sample)));
    }

    public void Dispose()
    {
        if (!dirty) return;
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(entries));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Usage completeness depends on the scan, not saving this index.
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal sealed record Signature(long Length, long CreatedTicks, long WrittenTicks, string BoundaryHash);
    internal sealed record Entry(Signature Signature, DateTimeOffset? First, DateTimeOffset? Last);
}
