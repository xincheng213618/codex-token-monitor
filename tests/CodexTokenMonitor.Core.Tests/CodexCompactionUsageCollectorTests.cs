using System.Text.Json;
using Xunit;

namespace CodexTokenMonitor.Core.Tests;

public sealed class CodexCompactionUsageCollectorTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 22, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OnlyAStandaloneRecordOwnedByTheFirstThreadAndLinkedToAMarkerIsIncluded(bool markerFirst)
    {
        var collector = StartRollout();
        Feed(collector, 1, "session_meta", new { id = "foreign" });
        Modern(collector, 2, "ordinary");
        Modern(collector, 3, "replayed", "foreign");
        Compacted(collector, 4, "replayed");
        Modern(collector, 5, "zero", total: 0);
        Compacted(collector, 6, "zero");
        if (markerFirst) Compacted(collector, 7, "compaction");

        Modern(collector, 8, "compaction");
        if (!markerFirst) Compacted(collector, 9, "compaction");
        Modern(collector, 10, "compaction");
        Compacted(collector, 11, "compaction");

        var result = Assert.Single(collector.Resolve());
        Assert.False(result.HasLegacy);
        Assert.Equal("codex-compaction:owner:compaction", result.Event.Key);
        Assert.Equal(Start.AddMilliseconds(8), result.Event.Timestamp);
        Assert.Equal(CodexUsageReader.BeijingOffset, result.Event.Timestamp.Offset);
        Assert.Equal("fixture-model", result.Event.ModelId);
        Assert.Equal("priority", result.Event.ServiceTier);
        Assert.Equal(135L, result.Event.TotalTokens);
        Assert.Equal(120L, result.Event.InputTokens);
        Assert.Equal(41L, result.Event.CachedInputTokens);
        Assert.Equal(10L, result.Event.CacheWriteInputTokens);
        Assert.Equal(15L, result.Event.OutputTokens);
        Assert.Equal(9L, result.Event.ReasoningOutputTokens);
    }

    [Fact]
    public void ExplicitLegacyResponseIdUpdatesResolutionEvenIfCountersAndTurnDiffer()
    {
        var collector = StartRollout();
        Modern(collector, 5, "compaction");
        Assert.Empty(collector.Resolve());
        Compacted(collector, 6, "compaction");
        var first = Assert.Single(collector.Resolve());
        Assert.False(first.HasLegacy);

        Legacy(collector, 7, "other", "turn");
        Assert.False(Assert.Single(collector.Resolve()).HasLegacy);

        Legacy(collector, 8, "compaction", "another-turn", cached: 40);
        var later = Assert.Single(collector.Resolve());
        Assert.True(later.HasLegacy);
        Assert.Equal(first.Event, later.Event);
    }

    [Fact]
    public void AnonymousLegacyPrefersTheNearestModernRecordEvenIfItIsOrdinary()
    {
        var collector = StartRollout();
        Modern(collector, 5, "first-compaction");
        Compacted(collector, 6, "first-compaction");
        Modern(collector, 7, "ordinary");
        Legacy(collector, 8);
        Modern(collector, 3_005, "second-compaction");
        Compacted(collector, 3_006, "second-compaction");
        Legacy(collector, 3_008);

        Assert.Collection(collector.Resolve(),
            item => Assert.False(item.HasLegacy),
            item => Assert.True(item.HasLegacy));
    }

    [Fact]
    public void AppendingAnotherModernRecordDoesNotInvalidateAnAnonymousPair()
    {
        var collector = StartRollout();
        Modern(collector, 5, "compaction");
        Compacted(collector, 6, "compaction");
        Legacy(collector, 7);
        Assert.True(Assert.Single(collector.Resolve()).HasLegacy);

        Modern(collector, 8, "ordinary");
        Assert.True(Assert.Single(collector.Resolve()).HasLegacy);
    }

    [Fact]
    public void AnonymousLookalikeInADifferentTurnOrFarFromTheModernRecordDoesNotPair()
    {
        var collector = StartRollout();
        Modern(collector, 5, "compaction");
        Compacted(collector, 6, "compaction");
        Legacy(collector, 6, cached: 40);
        Feed(collector, 7, "turn_context", new { turn_id = "another-turn" });
        Legacy(collector, 8);
        Feed(collector, 2_505, "turn_context", new { turn_id = "turn" });
        Legacy(collector, 2_506);

        Assert.False(Assert.Single(collector.Resolve()).HasLegacy);
    }

    private static CodexCompactionUsageCollector StartRollout()
    {
        var collector = new CodexCompactionUsageCollector();
        Feed(collector, 0, "session_meta", new { id = "owner" });
        Feed(collector, 0, "event_msg", new { type = "task_started", turn_id = "turn" });
        return collector;
    }

    private static void Modern(
        CodexCompactionUsageCollector collector, int milliseconds, string response, string thread = "owner", long total = 135) =>
        Feed(collector, milliseconds, "token_usage_record", new
        {
            thread_id = thread,
            response_id = response,
            turn_id = "turn",
            usage = new
            {
                input_tokens = total == 0 ? 0 : 120,
                cached_input_tokens = total == 0 ? 0 : 41,
                cache_write_input_tokens = total == 0 ? 0 : 10,
                output_tokens = total == 0 ? 0 : 15,
                reasoning_output_tokens = total == 0 ? 0 : 9,
                total_tokens = total
            }
        }, modelId: "fixture-model", serviceTier: "Priority");

    private static void Compacted(CodexCompactionUsageCollector collector, int milliseconds, string response) =>
        Feed(collector, milliseconds, "compacted", new { compaction_response_id = response });

    private static void Legacy(CodexCompactionUsageCollector collector, int milliseconds,
        string? response = null, string? turn = null, long cached = 41) =>
        Feed(collector, milliseconds, "event_msg", new { type = "token_count", response_id = response, turn_id = turn },
            new(Start.AddMilliseconds(milliseconds), 120, cached, 15, 9, 135, CacheWriteInputTokens: 10));

    private static void Feed(CodexCompactionUsageCollector collector, int milliseconds, string type, object payload,
        TokenUsageEvent? acceptedLegacy = null, string? modelId = null, string? serviceTier = null) =>
        collector.Observe(JsonSerializer.Serialize(new { timestamp = Start.AddMilliseconds(milliseconds), type, payload }),
            acceptedLegacy, modelId, serviceTier);
}
