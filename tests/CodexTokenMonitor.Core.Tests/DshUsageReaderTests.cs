using System.Text;
using Xunit;
using ZstdSharp;

namespace CodexTokenMonitor.Tests;

public sealed class DshUsageReaderTests : IDisposable
{
    private readonly string testRoot = Path.Combine(Path.GetTempPath(), $"DshReaderTests-{Guid.NewGuid():N}");

    // UsageLogPaths resolves the DSH sessions root as <scope root>/Dsh, so the
    // transcript tree lives one level below the pushed scope like real logs do.
    private readonly IDisposable logScope;

    // The range readers write a day cache through UsageCacheStore. Without an
    // isolated root that cache lives in the machine's real %LOCALAPPDATA% and
    // survives the run, so a later run reuses a "complete" day the previous run
    // recorded and never rescans the synthetic transcript.
    private readonly IDisposable cacheScope;
    private readonly string cacheRoot = Path.Combine(Path.GetTempPath(), $"DshReaderCache-{Guid.NewGuid():N}");

    private string SessionsRoot => Path.Combine(testRoot, UsageSource.Dsh.ToString());

    public DshUsageReaderTests()
    {
        Directory.CreateDirectory(cacheRoot);
        logScope = UsageLogPaths.PushRoot(testRoot);
        cacheScope = MonitorCachePaths.PushLocalAppDataRoot(cacheRoot);
    }

    public void Dispose()
    {
        cacheScope.Dispose();
        logScope.Dispose();
        UsageCacheStore.Delete("DshTokenMonitor");
        foreach (var directory in new[] { testRoot, cacheRoot })
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // Best-effort cleanup of the temporary tree.
            }
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
    public void PreservesCacheWriteTokensAsIndependentInput()
    {
        var start = new DateTimeOffset(2026, 8, 13, 0, 0, 0, TimeSpan.FromHours(8));
        var timestamp = new DateTimeOffset(2026, 8, 13, 10, 30, 0, TimeSpan.FromHours(8));
        WriteTranscript(
            "--C-work--",
            "session-cache-write",
            Header(),
            UsageLine(1, timestamp, input: 1000, cached: 500, output: 200, reasoning: 50, cacheWrite: 250));

        var row = Assert.Single(DshUsageReader.ReadTransientDetailRows(start, start.AddDays(1)));

        Assert.Equal(1750, row.InputTokens);
        Assert.Equal(500, row.CachedInputTokens);
        Assert.Equal(250, row.CacheWriteInputTokens);
        Assert.Equal(1000, row.UncachedInputTokens);
    }

    [Fact]
    public void AttributesUsageToRequestModelAcrossFramesAndModelChanges()
    {
        var start = new DateTimeOffset(2026, 8, 13, 0, 0, 0, TimeSpan.FromHours(8));
        var timestamp = start.AddHours(10);
        var header = "{\"type\":\"request/header\",\"seq\":2,\"data\":{\"header\":{\"config\":{\"model\":\"deepseek-v4-flash\"}}}}\n";
        var context = "{\"type\":\"request/context\",\"seq\":4,\"data\":{\"model\":\"deepseek-v4-pro\"}}\n";
        WriteTranscript("--C-work--", "session-models", Header(),
            UsageLine(1, timestamp, 10, 0, 1, 0) + header + UsageLine(3, timestamp.AddMinutes(1), 100, 50, 10, 0),
            context + UsageLine(5, timestamp.AddMinutes(2), 200, 0, 20, 0));

        var rows = DshUsageReader.ReadTransientDetailRows(start, start.AddDays(1));

        Assert.Empty(rows[0].ModelUsage);
        Assert.Equal("deepseek-v4-flash", Assert.Single(rows[1].ModelUsage).Key);
        Assert.Equal("deepseek-v4-pro", Assert.Single(rows[2].ModelUsage).Key);
        Assert.Equal(3, rows.Sum(row => row.Events));
    }

    [Fact]
    public void RangeAndCacheRetainDshModelUsage()
    {
        var start = new DateTimeOffset(2026, 8, 13, 0, 0, 0, TimeSpan.FromHours(8));
        var timestamp = start.AddHours(10);
        var context = "{\"type\":\"request/context\",\"seq\":1,\"data\":{\"provider\":\"deepseek-official\",\"model\":\"deepseek-v4-flash\"}}\n";
        WriteTranscript("--C-work--", "session-model-cache", Header(),
            context + UsageLine(2, timestamp, 100, 500, 20, 0));

        var cacheRoot = Path.Combine(Path.GetTempPath(), $"DshModelCacheTests-{Guid.NewGuid():N}");
        using var cacheScope = MonitorCachePaths.PushLocalAppDataRoot(cacheRoot);
        try
        {
            var summary = DshUsageReader.ReadRange(start, start.AddDays(1), includeLiveToday: false);
            Assert.Equal("deepseek-v4-flash", Assert.Single(summary.ModelUsage).Key);
            Assert.Equal(620, Assert.Single(summary.ModelUsage).Value.TotalTokens);

            Assert.Equal("deepseek-v4-flash",
                Assert.Single(Assert.Single(DshUsageReader.ReadDetailRows(start, start.AddDays(1), includeLiveToday: false))
                    .ModelUsage).Key);
            var cached = DshUsageReader.ReadCachedRange(start, start.AddDays(1));
            Assert.Equal(620, Assert.Single(cached.ModelUsage).Value.TotalTokens);
            Assert.Equal("deepseek-v4-flash",
                Assert.Single(DshUsageReader.ReadCachedDetailRows(start, start.AddDays(1))
                    .SelectMany(row => row.ModelUsage.Keys)));
        }
        finally
        {
            UsageCacheStore.Delete("DshTokenMonitor");
            var cacheDirectory = Path.Combine(cacheRoot, "DshTokenMonitor");
            if (Directory.Exists(cacheDirectory)) Directory.Delete(cacheDirectory);
            if (Directory.Exists(cacheRoot)) Directory.Delete(cacheRoot);
        }
    }

    [Fact]
    public void DeepSeekCatalogIdsMatchDshRuntimeModels()
    {
        Assert.Equal("deepseek-v4-flash", CodexModelCost.DefaultModelId("DeepSeek", "V4.1 Flash"));
        Assert.Equal("deepseek-v4-pro", CodexModelCost.DefaultModelId("DeepSeek", "V4 Pro"));
    }

    [Fact]
    public void HarnessRouteIdsAliasToThePricedCatalogIds()
    {
        // dsh v4 settles under its own short route id while the price catalog
        // and released-format transcripts use the published API id; without the
        // alias the DSH "实际模型" card cannot price anything.
        Assert.Equal("deepseek-v4-flash", CodexModelCost.NormalizeModelId("deepseek-flash"));
        Assert.Equal("deepseek-v4-pro", CodexModelCost.NormalizeModelId("deepseek-pro"));
        Assert.Equal("deepseek-v4-flash", CodexModelCost.NormalizeModelId("deepseek-account/deepseek-flash"));
        Assert.Equal("deepseek-v4-flash", CodexModelCost.NormalizeModelId("DeepSeek-Flash"));
        // Released-format ids and unrelated models stay untouched.
        Assert.Equal("deepseek-v4-flash", CodexModelCost.NormalizeModelId("deepseek-v4-flash"));
        Assert.Equal("deepseek-chat", CodexModelCost.NormalizeModelId("deepseek-chat"));
    }

    [Fact]
    public void PricesCurrentFormatUsageThroughTheAlias()
    {
        var start = new DateTimeOffset(2026, 8, 13, 0, 0, 0, TimeSpan.FromHours(8));
        // 03:00 is inside DeepSeek's off-peak window, so the preset's base rate
        // applies without the working-hours peak multiplier: one million
        // uncached input tokens cost exactly the preset's input rate.
        var timestamp = new DateTimeOffset(2026, 8, 13, 3, 0, 0, TimeSpan.FromHours(8));
        WriteTranscript("--C-work--", "session-v4-priced", Header(),
            CurrentFormatUsageLine(1, timestamp, input: 1_000_000, cached: 0, output: 0, model: "deepseek-flash"));

        var summary = DshUsageReader.ReadRange(start, start.AddDays(1), includeLiveToday: false);

        // The shipped DSH preset catalog prices "deepseek-v4-flash" while the
        // harness reports "deepseek-flash"; the alias must bridge them, which is
        // what makes the DSH "实际模型 · 标准 API 等价" card non-zero.
        var flashPreset = Assert.Single(
            PricePreset.DefaultsForGroup(PricePresetGroups.Dsh),
            preset => preset.ModelId == "deepseek-v4-flash");

        var estimate = CodexModelCost.Estimate(summary, PricePresetGroups.Dsh);
        var line = Assert.Single(estimate.Models);
        Assert.Equal("deepseek-flash", line.ModelId);
        Assert.NotNull(line.Cost);
        Assert.Equal(flashPreset.UncachedInput, line.Cost);
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
    public void TruncatedTailFrameDoesNotMarkHistoricalDayComplete()
    {
        var start = new DateTimeOffset(2026, 8, 13, 0, 0, 0, TimeSpan.FromHours(8));
        var timestamp = new DateTimeOffset(2026, 8, 13, 10, 30, 0, TimeSpan.FromHours(8));
        var file = WriteTranscript("--C-work--", "session-cache-health", Header(), UsageLine(1, timestamp, 1000, 500, 200, 50));
        var fullFrame = CompressFrame(UsageLine(2, timestamp.AddMinutes(1), 700, 0, 60, 10));
        AppendBytes(file, fullFrame.Take(fullFrame.Length / 2).ToArray());

        var cacheRoot = Path.Combine(Path.GetTempPath(), $"DshReaderCacheTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(cacheRoot);
        using var cacheScope = MonitorCachePaths.PushLocalAppDataRoot(cacheRoot);
        try
        {
            var summary = DshUsageReader.ReadRange(start, start.AddDays(1), includeLiveToday: false);

            Assert.Equal(1, summary.Events);
            var cache = UsageCacheStore.Load("DshTokenMonitor");
            Assert.True(cache.TryGetRecord(DateOnly.FromDateTime(start.DateTime), out var record));
            Assert.False(record.IsComplete);
        }
        finally
        {
            UsageCacheStore.Delete("DshTokenMonitor");
            try
            {
                Directory.Delete(cacheRoot, recursive: true);
            }
            catch
            {
                // Best-effort cleanup of the unique temporary cache root.
            }
        }
    }

    [Fact]
    public void StreamsFramesAcrossReadBufferBoundaries()
    {
        var start = new DateTimeOffset(2026, 8, 13, 0, 0, 0, TimeSpan.FromHours(8));
        var firstTimestamp = new DateTimeOffset(2026, 8, 13, 10, 30, 0, TimeSpan.FromHours(8));
        var secondTimestamp = firstTimestamp.AddMinutes(1);
        var firstFrame = Header() + UsageLineWithPadding(1, firstTimestamp, 1000, 500, 200, 50, 100_000);
        var file = WriteTranscript("--C-work--", "session-test", firstFrame);
        AppendBytes(file, CompressFrame(UsageLine(2, secondTimestamp, 300, 0, 80, 10)));

        var rows = DshUsageReader.ReadTransientDetailRows(start, start.AddDays(1));

        Assert.Equal(2, rows.Count);
        Assert.Equal(1000 + 500 + 300, rows.Sum(item => item.InputTokens));
        Assert.Equal(200 + 80, rows.Sum(item => item.OutputTokens));
    }

    [Fact]
    public void SaturatesExtremeTokenComponentsInsteadOfWrapping()
    {
        var start = new DateTimeOffset(2026, 8, 13, 0, 0, 0, TimeSpan.FromHours(8));
        var timestamp = new DateTimeOffset(2026, 8, 13, 10, 30, 0, TimeSpan.FromHours(8));
        WriteTranscript(
            "--C-work--",
            "session-extreme",
            Header(),
            UsageLine(1, timestamp, long.MaxValue, long.MaxValue, long.MaxValue, long.MaxValue));

        var row = Assert.Single(DshUsageReader.ReadTransientDetailRows(start, start.AddDays(1)));

        Assert.Equal(long.MaxValue, row.InputTokens);
        Assert.Equal(long.MaxValue, row.CachedInputTokens);
        Assert.Equal(long.MaxValue, row.OutputTokens);
        Assert.Equal(long.MaxValue, row.ReasoningOutputTokens);
        Assert.Equal(long.MaxValue, row.TotalTokens);
    }

    [Fact]
    public void IgnoredRecordsDoNotCount()
    {
        var start = new DateTimeOffset(2026, 8, 13, 0, 0, 0, TimeSpan.FromHours(8));
        var timestamp = new DateTimeOffset(2026, 8, 13, 10, 30, 0, TimeSpan.FromHours(8));
        var header = Header();
        // A text chunk, a usage-less assistant message and a malformed line are
        // ignored; only settlements that carry token accounting count.
        var message = "{\"type\":\"assistant/message\",\"seq\":5,\"time\":" + ToMilliseconds(timestamp) +
                      ",\"data\":{\"turn\":1,\"step\":1,\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"hi\"}]}}}\n";
        var textChunk = "{\"type\":\"assistant/chunk\",\"seq\":6,\"time\":" + ToMilliseconds(timestamp) +
                        ",\"data\":{\"turn\":1,\"step\":1,\"chunk\":{\"type\":\"text\",\"delta\":\"hi\"}}}\n";
        var malformed = "{\"type\":\"assistant/chunk\",\"seq\":7\n";
        var file = WriteTranscript("--C-work--", "session-test", header, message + textChunk + malformed);

        var rows = DshUsageReader.ReadTransientDetailRows(start, start.AddDays(1));

        Assert.Empty(rows);
    }

    [Fact]
    public void ParsesCurrentFormatAssistantMessageUsage()
    {
        var start = new DateTimeOffset(2026, 8, 13, 0, 0, 0, TimeSpan.FromHours(8));
        var timestamp = new DateTimeOffset(2026, 8, 13, 10, 30, 0, TimeSpan.FromHours(8));
        // dsh v4 settlements carry the usage sample on assistant/message (the
        // same sample the harness folds into its own tokenUsage projection) and
        // name the model on the message source instead of a request record.
        var first = CurrentFormatUsageLine(1, timestamp, input: 7803, cached: 1024, output: 131, model: "deepseek-flash");
        var second = CurrentFormatUsageLine(2, timestamp.AddMinutes(1), input: 300, cached: 18944, output: 325, model: "deepseek-flash");

        WriteTranscript("--C-work--", "session-v4", Header(), first + second);

        var rows = DshUsageReader.ReadTransientDetailRows(start, start.AddDays(1));

        Assert.Equal(2, rows.Count);
        Assert.Equal(7803 + 1024 + 300 + 18944, rows.Sum(item => item.InputTokens));
        Assert.Equal(1024 + 18944, rows.Sum(item => item.CachedInputTokens));
        Assert.Equal(7803 + 300, rows.Sum(item => item.UncachedInputTokens));
        Assert.Equal(131 + 325, rows.Sum(item => item.OutputTokens));
        Assert.Equal(0, rows.Sum(item => item.ReasoningOutputTokens));
        Assert.Equal(7803 + 1024 + 131 + 300 + 18944 + 325, rows.Sum(item => item.TotalTokens));
        Assert.All(rows, row => Assert.Equal("deepseek-flash", Assert.Single(row.ModelUsage).Key));
    }

    [Fact]
    public void ReadsOnlyTheHighestTranscriptGeneration()
    {
        var start = new DateTimeOffset(2026, 8, 13, 0, 0, 0, TimeSpan.FromHours(8));
        var timestamp = new DateTimeOffset(2026, 8, 13, 10, 30, 0, TimeSpan.FromHours(8));
        // Migration retains the superseded generation beside its successor, so
        // a migrated session directory holds both files and only v4 may be
        // counted, while an unmigrated session keeps contributing through its
        // released-generation name.
        WriteTranscriptName("--C-work--", "session-migrated", "session.jsonl.zstd",
            Header() + UsageLine(1, timestamp, 1000, 0, 100, 0));
        WriteTranscriptName("--C-work--", "session-migrated", "session.v4.jsonl.zstd",
            Header() + CurrentFormatUsageLine(2, timestamp.AddMinutes(1), 1000, 0, 100, "deepseek-flash"));
        WriteTranscriptName("--C-work--", "session-unmigrated", "session.jsonl.zstd",
            Header() + UsageLine(1, timestamp.AddMinutes(2), 7, 0, 3, 0));

        var rows = DshUsageReader.ReadTransientDetailRows(start, start.AddDays(1));

        Assert.Equal(2, rows.Count);
        Assert.Equal(2, rows.Sum(row => row.Events));
        Assert.Equal(1100 + 10, rows.Sum(row => row.TotalTokens));
        Assert.Equal("deepseek-flash", Assert.Single(rows[0].ModelUsage).Key);
        Assert.Empty(rows[1].ModelUsage);
    }

    [Fact]
    public void ReadsUncompressedTranscriptGeneration()
    {
        var start = new DateTimeOffset(2026, 8, 13, 0, 0, 0, TimeSpan.FromHours(8));
        var timestamp = new DateTimeOffset(2026, 8, 13, 10, 30, 0, TimeSpan.FromHours(8));
        // `compression: 'none'` keeps the logical lines but drops the suffix.
        WriteRawTranscript("--C-work--", "session-raw", "session.v4.jsonl",
            Header() + CurrentFormatUsageLine(1, timestamp, 500, 250, 40, "deepseek-flash"));

        var row = Assert.Single(DshUsageReader.ReadTransientDetailRows(start, start.AddDays(1)));

        Assert.Equal(750, row.InputTokens);
        Assert.Equal(250, row.CachedInputTokens);
        Assert.Equal(500, row.UncachedInputTokens);
        Assert.Equal(40, row.OutputTokens);
    }

    [Fact]
    public void CurrentFormatMessageAttributesModelWithoutRequestRecord()
    {
        var start = new DateTimeOffset(2026, 8, 13, 0, 0, 0, TimeSpan.FromHours(8));
        var timestamp = new DateTimeOffset(2026, 8, 13, 10, 30, 0, TimeSpan.FromHours(8));
        // A v4 transcript names the route on each settlement, so attribution
        // survives a session that never wrote a request/context record.
        WriteTranscript("--C-work--", "session-v4-model", Header(),
            CurrentFormatUsageLine(1, timestamp, 100, 50, 10, "deepseek-v4-pro"));

        var row = Assert.Single(DshUsageReader.ReadTransientDetailRows(start, start.AddDays(1)));

        Assert.Equal("deepseek-v4-pro", Assert.Single(row.ModelUsage).Key);
        Assert.Equal(160, row.TotalTokens);
    }

    [Fact]
    public void VersionedTranscriptNamesAreRecognized()
    {
        var start = new DateTimeOffset(2026, 8, 13, 0, 0, 0, TimeSpan.FromHours(8));
        var timestamp = new DateTimeOffset(2026, 8, 13, 10, 30, 0, TimeSpan.FromHours(8));
        var header = Header();
        // Every released generation name must still be readable on its own,
        // in both compressed and raw (compression: 'none') encodings.
        var legacy = header + UsageLine(1, timestamp, 100, 0, 10, 0);
        var current = header + CurrentFormatUsageLine(1, timestamp, 100, 0, 10, "deepseek-flash");
        WriteTranscriptName("--C-work--", "session-name-0", "session.jsonl.zstd", legacy);
        WriteTranscriptName("--C-work--", "session-name-1", "session.v3.jsonl.zstd", legacy);
        WriteTranscriptName("--C-work--", "session-name-2", "session.v4.jsonl.zstd", current);
        WriteRawTranscript("--C-work--", "session-name-3", "session.v4.jsonl", current);

        var rows = DshUsageReader.ReadTransientDetailRows(start, start.AddDays(1));

        Assert.Equal(4, rows.Sum(row => row.Events));
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
        long reasoning,
        long cacheWrite = 0)
    {
        return "{\"type\":\"assistant/chunk\",\"seq\":" + seq + ",\"time\":" + ToMilliseconds(timestamp) +
               ",\"data\":{\"turn\":1,\"step\":1,\"chunk\":{\"type\":\"usage\",\"usage\":{" +
               "\"inputTokens\":" + input + ",\"outputTokens\":" + output +
               ",\"cacheReadTokens\":" + cached + ",\"cacheWriteTokens\":" + cacheWrite +
               ",\"reasoningTokens\":" + reasoning + "}}}}\n";
    }

    private static string UsageLineWithPadding(
        long seq,
        DateTimeOffset timestamp,
        long input,
        long cached,
        long output,
        long reasoning,
        int paddingLength)
    {
        var line = UsageLine(seq, timestamp, input, cached, output, reasoning).TrimEnd('\n');
        var random = new Random(17);
        var bytes = new byte[paddingLength];
        random.NextBytes(bytes);
        var padding = Convert.ToBase64String(bytes);
        return line[..^1] + ",\"padding\":\"" + padding + "\"}\n";
    }

    /// <summary>
    /// A current-format (dsh v4) settlement: token accounting on
    /// `assistant/message.data.usage` and the producing model on the message
    /// source, exactly as the harness writes it.
    /// </summary>
    private static string CurrentFormatUsageLine(
        long seq,
        DateTimeOffset timestamp,
        long input,
        long cached,
        long output,
        string model,
        long cacheWrite = 0)
    {
        return "{\"type\":\"assistant/message\",\"seq\":" + seq + ",\"time\":" + ToMilliseconds(timestamp) +
               ",\"data\":{\"turn\":1,\"step\":1,\"message\":{\"role\":\"assistant\"," +
               "\"content\":[{\"type\":\"text\",\"text\":\"hi\"}]," +
               "\"source\":{\"kind\":\"model\",\"provider\":\"deepseek-account\",\"model\":\"" + model + "\"}}," +
               "\"usage\":{\"inputTokens\":" + input + ",\"outputTokens\":" + output +
               ",\"cacheReadTokens\":" + cached + ",\"cacheWriteTokens\":" + cacheWrite + "," +
               "\"totalTokens\":" + (input + cached + cacheWrite + output) + "}}}\n";
    }

    private static long ToMilliseconds(DateTimeOffset value)
    {
        return value.ToUnixTimeMilliseconds();
    }

    private string WriteTranscript(string projectDir, string sessionId, params string[] frames)
    {
        return WriteTranscriptName(projectDir, sessionId, "session.jsonl.zstd", frames);
    }

    private string WriteTranscriptName(string projectDir, string sessionId, string fileName, params string[] frames)
    {
        var directory = Path.Combine(SessionsRoot, projectDir, sessionId);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        var combined = new List<byte>();
        foreach (var frame in frames)
        {
            combined.AddRange(CompressFrame(frame));
        }

        File.WriteAllBytes(path, combined.ToArray());
        return path;
    }

    /// <summary>Writes a `compression: 'none'` transcript: plain JSONL lines.</summary>
    private string WriteRawTranscript(string projectDir, string sessionId, string fileName, string text)
    {
        var directory = Path.Combine(SessionsRoot, projectDir, sessionId);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
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
