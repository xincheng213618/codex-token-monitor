namespace CodexTokenMonitor;

/// <summary>
/// Reads complete lines appended since the previous pass. A cursor is kept per file;
/// truncation, replacement and explicit reset safely fall back to the beginning.
/// </summary>
internal sealed class LiveFileTailReader
{
    private readonly ConcurrentDictionary<string, FileCursor> cursors =
        new(StringComparer.OrdinalIgnoreCase);

    public void ReadNewLines(string file, DateTimeOffset coverageStart, Action<string> consumeLine)
    {
        var cursor = cursors.GetOrAdd(file, static _ => new FileCursor());
        lock (cursor.SyncRoot)
        {
            try
            {
                using var stream = OpenStream(file, coverageStart, cursor);
                if (stream is null)
                {
                    return;
                }

                using var reader = new StreamReader(stream);
                while (reader.ReadLine() is { } line)
                {
                    consumeLine(line);
                }

                Commit(stream, cursor);
            }
            catch
            {
                // Active files can be moved or replaced between enumeration and open.
            }
        }
    }

    public void Prime(string file, DateTimeOffset coverageStart)
    {
        var cursor = cursors.GetOrAdd(file, static _ => new FileCursor());
        lock (cursor.SyncRoot)
        {
            try
            {
                if (cursor.CoveredFrom is null || coverageStart < cursor.CoveredFrom)
                {
                    cursor.CoveredFrom = coverageStart;
                }
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                Commit(stream, cursor);
            }
            catch
            {
                cursors.TryRemove(file, out _);
            }
        }
    }

    public void Reset()
    {
        cursors.Clear();
    }

    private static FileStream? OpenStream(string file, DateTimeOffset coverageStart, FileCursor cursor)
    {
        var info = new FileInfo(file);
        var changedWithoutGrowth = info.Length == cursor.KnownLength &&
                                   info.LastWriteTimeUtc != cursor.LastWriteTimeUtc;
        var needsEarlierCoverage = cursor.CoveredFrom is not null && coverageStart < cursor.CoveredFrom;
        if (info.Length < cursor.Offset || changedWithoutGrowth || needsEarlierCoverage)
        {
            cursor.Offset = 0;
        }

        if (cursor.CoveredFrom is null || coverageStart < cursor.CoveredFrom)
        {
            cursor.CoveredFrom = coverageStart;
        }

        cursor.KnownLength = info.Length;
        cursor.LastWriteTimeUtc = info.LastWriteTimeUtc;
        if (info.Length <= cursor.Offset)
        {
            return null;
        }

        var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        stream.Seek(cursor.Offset, SeekOrigin.Begin);
        return stream;
    }

    private static void Commit(FileStream stream, FileCursor cursor)
    {
        var length = stream.Length;
        if (length == 0)
        {
            cursor.Offset = 0;
        }
        else
        {
            stream.Seek(-1, SeekOrigin.End);
            cursor.Offset = stream.ReadByte() == (byte)'\n' ? length : cursor.Offset;
        }

        cursor.KnownLength = length;
        cursor.LastWriteTimeUtc = File.GetLastWriteTimeUtc(stream.Name);
    }

    private sealed class FileCursor
    {
        public object SyncRoot { get; } = new();
        public long Offset { get; set; }
        public long KnownLength { get; set; }
        public DateTime LastWriteTimeUtc { get; set; }
        public DateTimeOffset? CoveredFrom { get; set; }
    }
}
