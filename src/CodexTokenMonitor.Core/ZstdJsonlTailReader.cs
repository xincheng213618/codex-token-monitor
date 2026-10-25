using System.Text;
using ZstdSharp;

namespace CodexTokenMonitor;

/// <summary>Commits complete zstd frames only. Partial frames and UTF-8/JSON lines survive the next append.</summary>
internal sealed class ZstdJsonlTailReader : IDisposable
{
    private long offset, knownLength;
    private DateTime writeTime;
    private bool initialized, incomplete;
    private readonly MemoryStream line = new();
    private bool dropLongLine;
    private const int MaxLineBytes = 32 * 1024 * 1024;
    private const int MaxFrameBytes = 256 * 1024 * 1024;

    public bool Read(string path, Action<string> consume, Action restart, CancellationToken token, bool allowFinalLine = false)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            var info = new FileInfo(path);
            if (initialized && info.Length == knownLength && info.LastWriteTimeUtc == writeTime) return !incomplete;
            if (!initialized || info.Length < offset || info.Length == knownLength && info.LastWriteTimeUtc != writeTime)
            {
                offset = 0; line.SetLength(0); dropLongLine = false; restart();
            }
            initialized = true;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Position = offset;
            using var frames = new MemoryStream(); using var decoder = new Decompressor();
            var chunk = new byte[64 * 1024]; var consumed = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (TryFrameLength(frames.GetBuffer().AsSpan(consumed, checked((int)frames.Length) - consumed), out var size))
                {
                    var frame = frames.GetBuffer().AsSpan(consumed, size);
                    // Skippable zstd frames contain metadata and produce no transcript bytes.
                    var magic = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(frame);
                    if ((magic & 0xfffffff0) != 0x184d2a50)
                        Consume(decoder.Unwrap(frame), consume, token);
                    consumed += size; offset += size;
                    continue;
                }
                if (consumed > 0)
                {
                    var remaining = checked((int)frames.Length) - consumed;
                    frames.GetBuffer().AsSpan(consumed, remaining).CopyTo(frames.GetBuffer());
                    frames.SetLength(remaining); frames.Position = remaining; consumed = 0;
                }
                if (frames.Length > MaxFrameBytes) return false;
                var read = stream.Read(chunk);
                if (read == 0)
                {
                    knownLength = stream.Length; writeTime = File.GetLastWriteTimeUtc(path);
                    if (allowFinalLine && frames.Length == 0 && line.Length > 0 && !dropLongLine)
                    {
                        try
                        {
                            using var document = JsonDocument.Parse(line.GetBuffer().AsMemory(0, checked((int)line.Length)));
                            consume(Encoding.UTF8.GetString(line.GetBuffer(), 0, checked((int)line.Length)));
                            line.SetLength(0);
                        }
                        catch (JsonException) { }
                    }
                    incomplete = frames.Length > 0 || line.Length > 0 || dropLongLine;
                    return !incomplete;
                }
                frames.Write(chunk, 0, read);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (ZstdException) { return false; }
    }

    private void Consume(ReadOnlySpan<byte> decoded, Action<string> consume, CancellationToken token)
    {
        foreach (var value in decoded)
        {
            if (value == (byte)'\n')
            {
                token.ThrowIfCancellationRequested();
                if (!dropLongLine)
                {
                    var text = Encoding.UTF8.GetString(line.GetBuffer(), 0, checked((int)line.Length)).TrimEnd('\r');
                    consume(text.TrimStart('\uFEFF'));
                }
                line.SetLength(0); dropLongLine = false;
                if (line.Capacity > 1024 * 1024) line.Capacity = 0;
            }
            else if (!dropLongLine)
            {
                if (line.Length >= MaxLineBytes) { line.SetLength(0); dropLongLine = true; }
                else line.WriteByte(value);
            }
        }
    }

    private static unsafe bool TryFrameLength(ReadOnlySpan<byte> bytes, out int length)
    {
        length = 0;
        if (bytes.Length < 4) return false;
        // The pointer is pinned only for the library's bounded frame scan; no pointer arithmetic is performed here.
        fixed (byte* data = bytes)
        {
            var size = ZstdSharp.Unsafe.Methods.ZSTD_findFrameCompressedSize(data, (nuint)bytes.Length);
            if (ZstdSharp.Unsafe.Methods.ZSTD_isError(size) || size > (nuint)bytes.Length || size > int.MaxValue) return false;
            length = (int)size; return length > 0;
        }
    }
    public void Dispose() => line.Dispose();
}
