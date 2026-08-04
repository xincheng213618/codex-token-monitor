using System.Buffers;
using System.Text;

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
        ReadNewLinesWhile(file, coverageStart, line =>
        {
            consumeLine(line);
            return true;
        });
    }

    /// <summary>
    /// Reads appended complete lines until <paramref name="consumeLine"/> rejects one.
    /// A rejected line is deliberately left uncommitted so a later pass can retry it.
    /// </summary>
    public void ReadNewLinesWhile(string file, DateTimeOffset coverageStart, Func<string, bool> consumeLine)
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

                var committedOffset = cursor.Offset;
                using var lineBuffer = new MemoryStream();
                var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
                try
                {
                    var stopped = false;
                    while (!stopped)
                    {
                        var bytesRead = stream.Read(buffer, 0, buffer.Length);
                        if (bytesRead == 0)
                        {
                            break;
                        }

                        var chunkStartOffset = stream.Position - bytesRead;
                        var segmentStart = 0;
                        for (var index = 0; index < bytesRead; index++)
                        {
                            if (buffer[index] != (byte)'\n')
                            {
                                continue;
                            }

                            lineBuffer.Write(buffer, segmentStart, index - segmentStart);
                            var line = Encoding.UTF8.GetString(lineBuffer.GetBuffer(), 0, checked((int)lineBuffer.Length));
                            if (line.EndsWith('\r'))
                            {
                                line = line[..^1];
                            }

                            if (committedOffset == 0 && line.StartsWith('\uFEFF'))
                            {
                                line = line[1..];
                            }

                            if (!consumeLine(line))
                            {
                                stopped = true;
                                break;
                            }

                            committedOffset = chunkStartOffset + index + 1;
                            lineBuffer.SetLength(0);
                            segmentStart = index + 1;
                        }

                        if (!stopped && segmentStart < bytesRead)
                        {
                            lineBuffer.Write(buffer, segmentStart, bytesRead - segmentStart);
                        }
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                Commit(stream, cursor, committedOffset);
            }
            catch
            {
                // Active files can be moved or replaced between enumeration and open.
            }
        }
    }

    public bool IsTracked(string file)
    {
        return cursors.ContainsKey(file);
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
                Commit(stream, cursor, stream.Length);
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

    private static void Commit(FileStream stream, FileCursor cursor, long committedOffset)
    {
        var length = stream.Length;
        cursor.Offset = Math.Min(committedOffset, length);
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
