using System.Text;
using Xunit;
using ZstdSharp;

namespace CodexTokenMonitor.Tests;

public sealed class DshUsageReaderTests : IDisposable
{
    private readonly string sessionsRoot = Path.Combine(Path.GetTempPath(), $"DshReaderTests-{Guid.NewGuid():N}");

    public DshUsageReaderTests()
    {
        DshUsageReader.OverrideSessionsRoot = sessionsRoot;
    }

    public void Dispose()
    {
        DshUsageReader.OverrideSessionsRoot = null;
        try
        {
            Directory.Delete(sessionsRoot, recursive: true);
        }
        catch
        {
            // Best-effort cleanup of the temporary tree.
        }
    }

    [Fact]
    public void ParsesUsageRecordsFromMultiFrameTranscript()
    {
        var start = new DateTimeOffset(2026, 8, 13, 0, 0, 0, TimeSpan.FromHours(8));
        var timestamp = new DateTimeOffset(2026, 8, 13, 10, 30, 0, TimeSpan.FromHours(8));
        var other = new DateTimeOffset(2026, 8, 13, 11, 0, 0, TimeSpan.FromHours(8));

        // Header frame + one append frame, mirroring dsh's on-disk layout.
        var header = "{\"type\":\"session\",\"version\":0,\"id\":\"session-test\",\"createdAt\":0,\"cwd\":\"C:\\\\work\",\"delegationDepth\":0,\"agentPreset\":\"standard\"}\n";
        var first = UsageLine(1, timestamp, input: 1000, cached: 5000, output: 200, reasoning: 50);
        var second = UsageLine(2, other, input: 300, cached: 0, output: 80, reasoning: 30);
        var file = WriteTranscript("--C-work--", "session-test", header, first);
        AppendBytes(file, CompressFrame(second));

        var rows = DshUsageReader.ReadTransientDetailRows(start, start.AddDays(1));

        Assert.Equal(2, rows.Count);
        var input = rows.Sum(item => item.InputTokens);
        var cached = rows.Sum(item => item.CachedInputTokens);
        var uncached = rows.Sum(item => item.UncachedInputTokens);
        var output = rows.Sum(item => item.OutputTokens);
        var reasoning = rows.Sum(item => item.ReasoningOutputTokens);
        var total = rows.Sum(item => item.TotalTokens);
        // Input includes cached reads (bucket pipeline computes Uncached = Input - Cached).
        Assert.Equal(1000 + 5000 + 300, input);
        Assert.Equal(5000, cached);
        Assert.Equal(1000 + 300, uncached);
        Assert.True(uncached >= 0);
        Assert.Equal(200 + 80, output);
        Assert.Equal(50 + 30, reasoning);
        Assert.Equal(1000 + 5000 + 200 + 300 + 0 + 80, total);
    }

    [Fact]
    public void RepeatedReadsDoNotDoubleCount()
    {
        var start = new DateTimeOffset(2026, 8, 13, 0, 0, 0, TimeSpan.FromHours(8));
        var timestamp = new DateTimeOffset(2026, 8, 13, 10, 30, 0, TimeSpan.FromHours(8));
        var file = WriteTranscript("--C-work--", "session-test", Header(), UsageLine(1, timestamp, 1000, 500, 200, 50));

        var first = DshUsageReader.ReadTransientDetailRows(start, start.AddDays(1));
        var second = DshUsageReader.ReadTransientDetailRows(start, start.AddDays(1));

        Assert.Equal(1, Assert.Single(first).Events);
        Assert.Equal(1, Assert.Single(second).Events);
        Assert.Equal(Assert.Single(first).TotalTokens, Assert.Single(second).TotalTokens);
    }

    [Fact]
    public void RecordsOutsideRangeAreFiltered()
    {
        var start = new DateTimeOffset(2026, 8, 13, 0, 0, 0, TimeSpan.FromHours(8));
        var inside = new DateTimeOffset(2026, 8, 13, 9, 0, 0, TimeSpan.FromHours(8));
        var outside = new DateTimeOffset(2026, 8, 14, 9, 0, 0, TimeSpan.FromHours(8));
        var file = WriteTranscript("--C-work--", "session-test", Header(), UsageLine(1, inside, 100, 0, 10, 0));
        AppendBytes(file, CompressFrame(UsageLine(2, outside, 999, 0, 99, 0)));

        var rows = DshUsageReader.ReadTransientDetailRows(start, start.AddDays(1));

        var total = Assert.Single(rows);
        Assert.Equal(1, total.Events);
        Assert.Equal(100, total.InputTokens);
    }

    [Fact]
    public void TruncatedTailFrameKeepsCompletePrefix()
    {
        var start = new DateTimeOffset(2026, 8, 13, 0, 0, 0, TimeSpan.FromHours(8));
        var timestamp = new DateTimeOffset(2026, 8, 13, 10, 30, 0, TimeSpan.FromHours(8));
        var file = WriteTranscript("--C-work--", "session-test", Header(), UsageLine(1, timestamp, 1000, 500, 200, 50));
        // Append a deliberately truncated frame (magic + partial payload).
        var fullFrame = CompressFrame(UsageLine(2, timestamp.AddMinutes(1), 700, 0, 60, 10));
        AppendBytes(file, fullFrame.Take(fullFrame.Length / 2).ToArray());

        var rows = DshUsageReader.ReadTransientDetailRows(start, start.AddDays(1));

        var total = Assert.Single(rows);
        Assert.Equal(1, total.Events);
        Assert.Equal(1000 + 500, total.InputTokens);
        Assert.Equal(1000, total.UncachedInputTokens);
    }

    [Fact]
    public void IgnoredRecordsDoNotCount()
    {
        var start = new DateTimeOffset(2026, 8, 13, 0, 0, 0, TimeSpan.FromHours(8));
        var timestamp = new DateTimeOffset(2026, 8, 13, 10, 30, 0, TimeSpan.FromHours(8));
        var header = Header();
        // A usage-carrying assistant/message is NOT counted (only usage chunks are);
        // a text chunk and a malformed line are ignored.
        var message = "{\"type\":\"assistant/message\",\"seq\":5,\"time\":" + ToMilliseconds(timestamp) +
                      ",\"data\":{\"turn\":1,\"step\":1,\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"hi\"}]},\"usage\":{\"inputTokens\":999,\"outputTokens\":1}}}\n";
        var textChunk = "{\"type\":\"assistant/chunk\",\"seq\":6,\"time\":" + ToMilliseconds(timestamp) +
                        ",\"data\":{\"turn\":1,\"step\":1,\"chunk\":{\"type\":\"text\",\"delta\":\"hi\"}}}\n";
        var malformed = "{\"type\":\"assistant/chunk\",\"seq\":7\n";
        var file = WriteTranscript("--C-work--", "session-test", header, message + textChunk + malformed);

        var rows = DshUsageReader.ReadTransientDetailRows(start, start.AddDays(1));

        Assert.Empty(rows);
    }

    [Fact]
    public void SourceReaderIsRegistered()
    {
        var reader = UsageSourceReaders.For(UsageSource.Dsh);

        Assert.Equal(UsageSource.Dsh, reader.Source);
        Assert.Equal("DSH", reader.Title);
        Assert.False(reader.SupportsQuota);
    }

    private static string Header()
    {
        return "{\"type\":\"session\",\"version\":0,\"id\":\"session-test\",\"createdAt\":0,\"cwd\":\"C:\\\\work\",\"delegationDepth\":0,\"agentPreset\":\"standard\"}\n";
    }

    private static string UsageLine(
        long seq,
        DateTimeOffset timestamp,
        long input,
        long cached,
        long output,
        long reasoning)
    {
        return "{\"type\":\"assistant/chunk\",\"seq\":" + seq + ",\"time\":" + ToMilliseconds(timestamp) +
               ",\"data\":{\"turn\":1,\"step\":1,\"chunk\":{\"type\":\"usage\",\"usage\":{" +
               "\"inputTokens\":" + input + ",\"outputTokens\":" + output +
               ",\"cacheReadTokens\":" + cached + ",\"reasoningTokens\":" + reasoning + "}}}}\n";
    }

    private static long ToMilliseconds(DateTimeOffset value)
    {
        return value.ToUnixTimeMilliseconds();
    }

    private string WriteTranscript(string projectDir, string sessionId, params string[] frames)
    {
        var directory = Path.Combine(sessionsRoot, projectDir, sessionId);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "session.jsonl.zstd");
        var combined = new List<byte>();
        foreach (var frame in frames)
        {
            combined.AddRange(CompressFrame(frame));
        }

        File.WriteAllBytes(path, combined.ToArray());
        return path;
    }

    private static byte[] CompressFrame(string text)
    {
        using var compressor = new Compressor();
        return compressor.Wrap(Encoding.UTF8.GetBytes(text)).ToArray();
    }

    private static void AppendBytes(string path, byte[] bytes)
    {
        using var stream = File.Open(path, FileMode.Append, FileAccess.Write);
        stream.Write(bytes, 0, bytes.Length);
    }
}
