using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class ClaudeQuotaEstimateTests : IDisposable
{
    private static readonly TimeSpan Beijing = TimeSpan.FromHours(8);
    private static readonly DateTimeOffset Day = new(2026, 10, 30, 0, 0, 0, Beijing);
    private readonly string root = Path.Combine(Path.GetTempPath(), $"ClaudeQuotaTests-{Guid.NewGuid():N}");
    private readonly IDisposable cacheScope;

    public ClaudeQuotaEstimateTests()
    {
        cacheScope = MonitorCachePaths.PushLocalAppDataRoot(root);
    }

    public void Dispose()
    {
        cacheScope.Dispose();
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch
        {
            // Best-effort cleanup of the temporary tree.
        }
    }

    private static TokenUsageBucket Row(DateTimeOffset at, long uncached, long output, string model)
    {
        var row = new TokenUsageBucket { StartLocal = at };
        row.Add(new TokenUsageEvent(at, uncached, 0, output, 0, uncached + output, $"k{at.Ticks}{model}", ModelId: model));
        return row;
    }

    [Fact]
    public void CreateReading_PricesOnlyClaudeModelsInsideTheWindow()
    {
        var rows = new[]
        {
            // Before the 10:00–15:00 window opened.
            Row(Day.AddHours(9), 1_000_000, 0, "claude-opus-5-5"),
            // Opus 5.5 default preset: $4 in, $20 out per million.
            Row(Day.AddHours(11), 1_000_000, 100_000, "claude-opus-5-5"),
            // MiMo runs through its own gateway and never draws on the Claude plan.
            Row(Day.AddHours(12), 5_000_000, 0, "mimo-v2.5-pro")
        };

        var reading = ClaudeQuotaEstimator.CreateReading(
            ClaudeQuotaWindowKind.FiveHour, 10, Day.AddHours(15), Day.AddHours(13), rows);

        Assert.Equal(Day.AddHours(10), reading.WindowStartLocal);
        Assert.Equal(6.00m, reading.CostUsd);
        Assert.Equal(1_100_000, reading.Tokens);
        Assert.Equal(60.00m, reading.CapacityUsd);
        Assert.Equal(11_000_000, reading.CapacityTokens);
        var range = reading.CapacityUsdRange!.Value;
        Assert.Equal(6m / 0.105m, range.Low);
        Assert.Equal(6m / 0.095m, range.High);
    }

    [Fact]
    public void Summarize_UsesEachWindowsFullestReadingAndSkipsTinyPercents()
    {
        ClaudeQuotaReading Reading(int window, double percent, decimal cost) => new(
            ClaudeQuotaWindowKind.FiveHour, Day.AddHours(window * 5).AddMinutes(percent), percent,
            Day.AddHours(window * 5), Day.AddHours(window * 5 + 5), cost, (long)(cost * 1_000_000), 10);

        var capacity = ClaudeQuotaEstimator.Summarize(new[]
        {
            Reading(0, 4, 3m),     // superseded by the 40% reading below
            Reading(0, 40, 32m),   // $80
            Reading(1, 20, 18m),   // $90
            Reading(2, 2, 10m),    // too coarse: would claim $500
            Reading(3, 50, 50m)    // $100
        }, ClaudeQuotaWindowKind.FiveHour)!;

        Assert.Equal(90m, capacity.CapacityUsd);
        Assert.Equal(3, capacity.Windows);
        Assert.False(capacity.IsTentative);
        Assert.Null(ClaudeQuotaEstimator.Summarize(Array.Empty<ClaudeQuotaReading>(), ClaudeQuotaWindowKind.Week));
    }

    [Fact]
    public void RecordOfficial_KeepsOneGrowingReadingPerWindow()
    {
        TokenUsageBucket Usage(long uncached)
        {
            var bucket = new TokenUsageBucket { StartLocal = Day.AddHours(10) };
            bucket.MergeFrom(Row(Day.AddHours(11), uncached, 0, "claude-opus-5-5"));
            return bucket;
        }

        ClaudeCodeActivitySnapshot Snapshot(double percent, long uncached) => new(
            Day.AddHours(12), Array.Empty<ClaudeCodeBlock>(), new TokenUsageBucket(), new TokenUsageBucket(), 0,
            FiveHourWindow: new ClaudeQuotaWindowUsage(ClaudeQuotaWindowKind.FiveHour, ClaudeWindowSource.Official,
                Day.AddHours(10), Day.AddHours(15), Usage(uncached), percent, true));

        Assert.True(ClaudeQuotaEstimator.RecordOfficial(Snapshot(10, 1_000_000), ClaudeQuotaReadingStore.Load()));
        Assert.False(ClaudeQuotaEstimator.RecordOfficial(Snapshot(10, 1_000_000), ClaudeQuotaReadingStore.Load()));
        Assert.True(ClaudeQuotaEstimator.RecordOfficial(Snapshot(20, 2_000_000), ClaudeQuotaReadingStore.Load()));

        var reading = Assert.Single(ClaudeQuotaReadingStore.Load());
        Assert.Equal(ClaudeQuotaReadingSource.Official, reading.Source);
        Assert.Equal(20, reading.UsedPercent);
        Assert.Equal(40.00m, reading.CapacityUsd);
    }

    [Fact]
    public void Store_KeepsACorruptFileInsteadOfOverwritingIt()
    {
        var folder = Path.Combine(root, "ClaudeCodeTokenMonitor");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "quota-readings.json"), "[{ not json");

        ClaudeQuotaReadingStore.Add(new ClaudeQuotaReading(ClaudeQuotaWindowKind.Week, Day, 12, Day.AddDays(-3), Day.AddDays(4), 120m, 900, 3));

        Assert.Single(ClaudeQuotaReadingStore.Load());
        var backup = Assert.Single(Directory.GetFiles(folder, "quota-readings.json.corrupt-*"));
        Assert.Equal("[{ not json", File.ReadAllText(backup));
    }

    [Fact]
    public void Store_ReadsReadingsSavedBeforeSourceExisted()
    {
        var folder = Path.Combine(root, "ClaudeCodeTokenMonitor");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "quota-readings.json"),
            "[{\"Kind\":\"FiveHour\",\"RecordedLocal\":\"2026-10-30T01:00:00+08:00\",\"UsedPercent\":24,\"WindowStartLocal\":\"2026-10-30T00:50:00+08:00\"," +
            "\"ResetLocal\":\"2026-10-30T05:50:00+08:00\",\"CostUsd\":6.57,\"Tokens\":18550000,\"Events\":92}]");

        var reading = Assert.Single(ClaudeQuotaReadingStore.Load());
        Assert.Equal(ClaudeQuotaReadingSource.Manual, reading.Source);
        Assert.Equal(24, reading.UsedPercent);
    }

    [Fact]
    public void Store_RoundTripsAndRemovesReadings()
    {
        var reading = new ClaudeQuotaReading(ClaudeQuotaWindowKind.Week, Day, 12, Day.AddDays(-3), Day.AddDays(4), 120m, 900, 3);
        ClaudeQuotaReadingStore.Add(reading);

        Assert.Equal(reading, Assert.Single(ClaudeQuotaReadingStore.Load()));
        ClaudeQuotaReadingStore.Remove(reading);
        Assert.Empty(ClaudeQuotaReadingStore.Load());
    }
}
