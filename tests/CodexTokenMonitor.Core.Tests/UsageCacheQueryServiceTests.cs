using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class UsageCacheQueryServiceTests
{
    private static readonly DateTimeOffset Day = new(2026, 9, 10, 0, 0, 0, TimeSpan.FromHours(8));
    private static readonly DateTimeOffset Now = Day.AddDays(2).AddHours(12);

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public void CachedOnlyCapabilitySupportsEveryRangeWithoutScanningOrRepair(int mode, bool custom)
    {
        var range = Range(mode, custom);
        var reader = Reader(range);
        using var cancellation = new CancellationTokenSource();

        var result = Service().ExecuteCached(new(reader, range, null), cancellation.Token);

        Assert.Equal(330, result.Summary.TotalTokens);
        Assert.Equal(3, result.Summary.Events);
        Assert.Same(reader.Rows, result.DetailRows);
        Assert.Equal(TimeSpan.FromMinutes(13), result.CodingTime);
        Assert.Equal(custom ? 0 : 1, reader.SummaryReads);
        Assert.Equal(1, reader.DetailReads);
        Assert.All(reader.Calls, call => Assert.Equal((range.Start, range.End, cancellation.Token), call));
        if (custom || mode == (int)RangeMode.Day)
            Assert.Same(reader.Rows, result.BreakdownRows);
        else if (mode == (int)RangeMode.Month)
        {
            Assert.Same(reader.Summary.DailyBuckets, result.BreakdownRows);
            Assert.Equal(330, Assert.Single(result.BreakdownRows).TotalTokens);
        }
        else
        {
            Assert.Equal(new long[] { 2, 1 }, result.BreakdownRows.Select(row => row.Events));
            Assert.Equal(new[] { Day.AddHours(9), Day.AddHours(9).AddMinutes(10) },
                result.BreakdownRows.Select(row => row.StartLocal));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void MissingDetailsPreserveCachedTotalsWithoutInventingCodingTime(int mode)
    {
        var range = Range(mode);
        var reader = Reader(range);
        reader.Rows = Array.Empty<TokenUsageBucket>();

        var result = Service().ExecuteCached(new(reader, range, null));

        Assert.Same(reader.Summary, result.Summary);
        Assert.Equal(330, result.Summary.TotalTokens);
        Assert.Empty(result.DetailRows);
        Assert.Equal(TimeSpan.Zero, result.CodingTime);
        if (mode == (int)RangeMode.Day) Assert.Empty(result.BreakdownRows);
        else
        {
            Assert.Same(reader.Summary.DailyBuckets, result.BreakdownRows);
            Assert.Equal(330, Assert.Single(result.BreakdownRows).TotalTokens);
        }
    }

    [Fact]
    public void CustomRangeIgnoresAggregateOutsideTheClippedEventRows()
    {
        var range = Range(3, true) with { Start = Day.AddHours(9).AddMinutes(6) };
        var reader = Reader(range);
        reader.Rows = reader.Rows.Skip(1).ToArray();

        var result = Service().ExecuteCached(new(reader, range, null));

        Assert.Equal(220, result.Summary.TotalTokens);
        Assert.Equal(2, result.Summary.Events);
        Assert.Equal(0, reader.SummaryReads);
        Assert.Equal(range.Start, result.Summary.StartLocal);
        Assert.Equal(range.End, result.Summary.EndLocal);
        Assert.Equal(TimeSpan.FromMinutes(7), result.CodingTime);
    }

    [Fact]
    public void CancellationAfterCacheReadCannotPublishLateSuccess()
    {
        var range = Range(0);
        var reader = Reader(range);
        using var cancellation = new CancellationTokenSource();
        reader.OnDetailRead = cancellation.Cancel;

        Assert.Throws<OperationCanceledException>(() =>
            Service().ExecuteCached(new(reader, range, null), cancellation.Token));
        Assert.Equal(1, reader.DetailReads);
    }

    [Fact]
    public void CancellationDuringFinalQuotaReadCannotPublishLateSuccess()
    {
        var range = Range(0);
        var reader = Reader(range);
        reader.SupportsQuota = true;
        using var cancellation = new CancellationTokenSource();
        var quota = new QuotaReader { OnTimelineRead = cancellation.Cancel };

        Assert.Throws<OperationCanceledException>(() =>
            Service(quota).ExecuteCached(new(reader, range, null), cancellation.Token));
        Assert.Equal(1, quota.TimelineReads);
    }

    [Fact]
    public void EmptyRangeDoesNoIoAndStillHonorsCancellation()
    {
        var range = Range(3) with { End = Day };
        var reader = Reader(range);
        reader.SupportsQuota = true;
        var quota = new QuotaReader();
        var service = Service(quota);

        var result = service.ExecuteCached(new(reader, range, null));

        Assert.Equal(0, result.Summary.TotalTokens);
        Assert.Empty(result.BreakdownRows);
        Assert.Empty(reader.Calls);
        Assert.Equal(0, quota.EstimateReads);
        Assert.Equal(0, quota.TimelineReads);
        Assert.Throws<OperationCanceledException>(() =>
            service.ExecuteCached(new(reader, range, null), new CancellationToken(true)));
    }

    [Fact]
    public void CacheDiagnosticsTravelWithResultAndClearOnSuccessfulRetry()
    {
        var range = Range(1);
        var reader = Reader(range);
        reader.OnDetailRead = () => CacheOperationDiagnostics.Report(
            "isolated-cache", "Read", new InvalidDataException("invalid cached data"));
        var service = Service();

        var failed = service.ExecuteCached(new(reader, range, null));
        Assert.Single(failed.CacheWarnings);
        reader.OnDetailRead = null;
        var recovered = service.ExecuteCached(new(reader, range, null));
        Assert.Empty(recovered.CacheWarnings);
        Assert.Equal(330, recovered.Summary.TotalTokens);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QuotaUsesSameFreshnessFallbackAndMaterializedAnchorContract(bool stale)
    {
        var range = Range(2);
        var reader = Reader(range);
        reader.SupportsQuota = true;
        var saved = new CodexQuotaEstimate(stale ? Now.AddDays(-1) : Now.AddMinutes(-1), "codex", "Codex", null, null);
        var quota = new QuotaReader();

        var result = Service(quota).ExecuteCached(new(reader, range, saved));

        Assert.Same(stale ? quota.Estimate : saved, result.Quota);
        Assert.Equal(stale ? 1 : 0, quota.EstimateReads);
        Assert.Equal(1, quota.TimelineReads);
        Assert.Equal(Day.AddDays(1).AddTicks(-1), Assert.Single(quota.Anchors));
        Assert.Equal(result.Quota!.SnapshotLocal, Assert.Single(quota.Supplemental).SnapshotLocal);
    }

    private static SelectedRange Range(int mode, bool custom = false) =>
        new(Day, Day.AddDays(1), "cached range", "details", (RangeMode)mode, custom);

    private static CacheReader Reader(SelectedRange range)
    {
        var rows = new[] { 1, 7, 14 }.Select(minute => new TokenUsageBucket
        {
            StartLocal = Day.AddHours(9).AddMinutes(minute),
            Events = 1, InputTokens = 100, OutputTokens = 10, TotalTokens = 110
        }).ToArray();
        var summary = UsageSummaryBuilder.FromRows(range.Start, range.End, rows);
        var dayBucket = new TokenUsageBucket { StartLocal = Day };
        foreach (var row in rows) dayBucket.MergeFrom(row);
        summary.DailyBuckets.Add(dayBucket);
        return new CacheReader { Rows = rows, Summary = summary };
    }

    private static UsageQueryService Service(QuotaReader? quota = null) => new(quota ?? new QuotaReader(), new FixedClock());
    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    // Deliberately has no scan or maintenance interface. The service cannot
    // request either capability from this caller, even with missing cache data.
    private sealed class CacheReader : IUsageCacheQuery
    {
        public UsageSource Source => UsageSource.Codex;
        public string Title => "cache-only source";
        public bool SupportsQuota { get; set; }
        public TokenUsageSummary Summary { get; init; } = new();
        public IReadOnlyList<TokenUsageBucket> Rows { get; set; } = Array.Empty<TokenUsageBucket>();
        public List<(DateTimeOffset Start, DateTimeOffset End, CancellationToken Token)> Calls { get; } = new();
        public Action? OnDetailRead { get; set; }
        public int SummaryReads { get; private set; }
        public int DetailReads { get; private set; }
        public TokenUsageSummary ReadCachedRange(DateTimeOffset startLocal, DateTimeOffset endLocal, CancellationToken cancellationToken = default)
        {
            SummaryReads++;
            Calls.Add((startLocal, endLocal, cancellationToken));
            return Summary;
        }
        public IReadOnlyList<TokenUsageBucket> ReadCachedDetailRows(DateTimeOffset startLocal, DateTimeOffset endLocal, CancellationToken cancellationToken = default)
        {
            DetailReads++;
            Calls.Add((startLocal, endLocal, cancellationToken));
            OnDetailRead?.Invoke();
            return Rows;
        }
        public IReadOnlyList<DateTimeOffset> GetIncompleteHistoricalDays(DateTimeOffset startInclusive, DateTimeOffset endInclusive, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Cached display queries do not decide whether to repair source history.");
    }

    private sealed class QuotaReader : IUsageQuotaReader
    {
        public CodexQuotaEstimate Estimate { get; } = new(Now, "codex", "Codex", null, null);
        public int EstimateReads { get; private set; }
        public int TimelineReads { get; private set; }
        public Action? OnTimelineRead { get; init; }
        public IReadOnlyList<DateTimeOffset> Anchors { get; private set; } = Array.Empty<DateTimeOffset>();
        public IReadOnlyList<CodexQuotaSnapshot> Supplemental { get; private set; } = Array.Empty<CodexQuotaSnapshot>();
        public CodexQuotaEstimate ReadCachedEstimate(CancellationToken cancellationToken = default)
        {
            EstimateReads++;
            return Estimate;
        }
        public IReadOnlyList<CodexQuotaSnapshot> ReadCachedTimeline(IReadOnlyList<DateTimeOffset> anchors,
            IReadOnlyList<CodexQuotaSnapshot> supplementalSnapshots, CancellationToken cancellationToken = default)
        {
            TimelineReads++;
            Anchors = anchors;
            Supplemental = supplementalSnapshots;
            OnTimelineRead?.Invoke();
            return supplementalSnapshots;
        }
    }
}
