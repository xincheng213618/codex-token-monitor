using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class UsageQueryServiceTests
{
    private static readonly DateTimeOffset Today = new(2026, 9, 12, 0, 0, 0, TimeSpan.FromHours(8));
    private static readonly DateTimeOffset Now = Today.AddHours(12);

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(0, true)]
    [InlineData(3, true)]
    public void CacheOnly_NeverScansSourceOrRepairsIncompleteDetails(int mode, bool customStart)
    {
        var range = Range((RangeMode)mode, Today.AddDays(-1), Now) with { IsCustomStart = customStart };
        var cachedRow = Row(range.Start.AddHours(9));
        var reader = new FakeUsageReader
        {
            Summary = Summary(range, cachedRow, Row(range.Start.AddHours(10))),
            Rows = new[] { cachedRow },
            IncompleteDays = new[] { range.Start }
        };
        var service = Service();

        // CacheOnly takes precedence even if a stale caller requests live data.
        var result = service.Execute(new(reader, range, CacheOnly: true, IncludeLiveToday: true, CachedQuota: null));

        Assert.Same(cachedRow, Assert.Single(result.DetailRows));
        Assert.Equal(customStart ? 1 : 2, result.Summary.Events);
        Assert.All(reader.Calls, call => Assert.Contains(call.Name, new[] { "cached-summary", "cached-details" }));
    }

    [Fact]
    public void CacheOnly_CurrentDayRetainsSummaryWhenDetailsAreMissing()
    {
        var range = Range(RangeMode.Day, Today, Now);
        var reader = new FakeUsageReader { Summary = Summary(range, Row(Today.AddHours(9))) };

        var result = Service().Execute(new(reader, range, true, false, null));

        Assert.Equal(1, result.Summary.Events);
        Assert.Empty(result.DetailRows);
        Assert.Equal(TimeSpan.Zero, result.CodingTime);
        Assert.DoesNotContain(reader.Calls, call => call.Name is "day" or "details" or "incomplete");
    }

    [Fact]
    public void CustomRange_NormalRefreshUsesTransientRowsAndPreservesEventMetadata()
    {
        var range = Range(RangeMode.Cycle, Today.AddDays(-1).AddHours(15), Now) with { IsCustomStart = true };
        var rows = new[] { Row(range.Start.AddMinutes(1)), Row(range.Start.AddMinutes(7)) };
        rows[0].ModelUsage["model-test"] = new TokenUsageBucket { Events = 1, TotalTokens = 110 };
        var reader = new FakeUsageReader { OnTransient = () => rows };

        var result = Service().Execute(new(reader, range, false, true, null));

        Assert.Same(rows, result.BreakdownRows);
        Assert.Same(rows, result.DetailRows);
        Assert.Equal(220, result.Summary.TotalTokens);
        Assert.Equal(110, result.Summary.ModelUsage["model-test"].TotalTokens);
        Assert.Equal(TimeSpan.FromMinutes(6), result.CodingTime);
        Assert.Equal("transient", Assert.Single(reader.Calls).Name);
        Assert.Equal(range.Start, reader.Calls[0].Start);
        Assert.Equal(range.End, reader.Calls[0].End);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HistoricalDay_RepairsMissingDetailsOrIncompleteCoverage(bool incompleteCoverage)
    {
        var range = Range(RangeMode.Day, Today.AddDays(-2), Today.AddDays(-1));
        var repairedRows = new[] { Row(range.Start.AddHours(9)), Row(range.Start.AddHours(9).AddMinutes(4)) };
        var reader = new FakeUsageReader
        {
            Summary = Summary(range, repairedRows),
            Rows = incompleteCoverage ? repairedRows : repairedRows.Take(1).ToArray(),
            IncompleteDays = incompleteCoverage ? new[] { range.Start } : Array.Empty<DateTimeOffset>(),
            OnDetails = (_, _) => repairedRows
        };
        reader.OnDay = () => new(reader.Summary, reader.Rows);

        var result = Service().Execute(new(reader, range, false, false, null));

        Assert.Equal(2, result.Summary.Events);
        Assert.Same(repairedRows, result.DetailRows);
        Assert.Equal(TimeSpan.FromMinutes(4), result.CodingTime);
        Assert.False(Assert.Single(reader.Calls, call => call.Name == "details").IncludeLiveToday);
    }

    [Fact]
    public void LiveDay_PassesLiveFlagAndDoesNotCheckHistoricalCompleteness()
    {
        var range = Range(RangeMode.Day, Today, Now);
        var rows = new[] { Row(Today.AddHours(9)) };
        var reader = new FakeUsageReader { OnDay = () => new(Summary(range, rows), rows) };

        var result = Service().Execute(new(reader, range, false, true, null));

        Assert.Equal(1, result.Summary.Events);
        Assert.True(Assert.Single(reader.Calls).IncludeLiveToday);
        Assert.Equal("day", reader.Calls[0].Name);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void WeekAndCycle_KeepTenMinuteBucketsAndUseEventTimesForCodingTime(int mode)
    {
        var range = Range((RangeMode)mode, Today.AddDays(-2), Today);
        var rows = new[]
        {
            Row(range.Start.AddHours(9).AddMinutes(1)),
            Row(range.Start.AddHours(9).AddMinutes(7)),
            Row(range.Start.AddHours(9).AddMinutes(14))
        };
        var reader = new FakeUsageReader { Summary = Summary(range, rows), Rows = rows };

        var result = Service().Execute(new(reader, range, true, false, null));

        Assert.Equal(new long[] { 2, 1 }, result.BreakdownRows.Select(row => row.Events));
        Assert.Equal(range.Start.AddHours(9), result.BreakdownRows[0].StartLocal);
        Assert.Equal(range.Start.AddHours(9).AddMinutes(10), result.BreakdownRows[1].StartLocal);
        Assert.Same(rows, result.DetailRows);
        Assert.Equal(TimeSpan.FromMinutes(13), result.CodingTime);
    }

    [Fact]
    public void Month_KeepsDailyBreakdownAndDetailedRowsForTimeline()
    {
        var range = Range(RangeMode.Month, Today.AddDays(-11), Today);
        var rows = new[] { Row(range.Start.AddHours(9)), Row(range.Start.AddHours(9).AddMinutes(5)) };
        var summary = Summary(range, rows);
        var daily = Row(range.Start);
        daily.Events = 2;
        summary.DailyBuckets.Add(daily);
        var reader = new FakeUsageReader { Summary = summary, Rows = rows };

        var result = Service().Execute(new(reader, range, true, false, null));

        Assert.Same(summary.DailyBuckets, result.BreakdownRows);
        Assert.Same(rows, result.DetailRows);
        Assert.Equal(TimeSpan.FromMinutes(5), result.CodingTime);
    }

    [Fact]
    public void HistoricalRange_RepairsBeforeReadingUpdatedDetails()
    {
        var range = Range(RangeMode.Week, Today.AddDays(-7), Today);
        var repairedRows = new[] { Row(range.Start.AddHours(9)), Row(range.Start.AddHours(10)) };
        var reader = new FakeUsageReader
        {
            Summary = Summary(range, repairedRows.Take(1).ToArray()),
            Rows = repairedRows.Take(1).ToArray(),
            IncompleteDays = new[] { range.Start }
        };
        reader.OnRange = () =>
        {
            reader.Rows = repairedRows;
            return Summary(range, repairedRows);
        };

        var result = Service().Execute(new(reader, range, false, false, null));

        Assert.Equal(2, result.Summary.Events);
        Assert.Same(repairedRows, result.DetailRows);
        Assert.Equal(new[] { "cached-summary", "cached-details", "incomplete", "range", "cached-details" },
            reader.Calls.Select(call => call.Name));
        Assert.False(reader.Calls.Single(call => call.Name == "range").IncludeLiveToday);
    }

    [Fact]
    public void RangeDetailRepair_ClipsEachDayToSelectedBoundsAndPropagatesCancellation()
    {
        var range = Range(RangeMode.Cycle, Today.AddDays(-2).AddHours(18), Today.AddDays(-1).AddHours(9));
        var first = Row(range.Start.AddMinutes(1));
        var second = Row(range.End.AddMinutes(-1));
        var reader = new FakeUsageReader
        {
            Summary = Summary(range, first, second),
            OnDetails = (start, _) => start == range.Start ? new[] { first } : new[] { second }
        };
        using var cts = new CancellationTokenSource();

        var result = Service().Execute(new(reader, range, false, false, null), cts.Token);

        Assert.Equal(new[] { first, second }, result.DetailRows);
        var calls = reader.Calls.Where(call => call.Name == "details").ToArray();
        Assert.Equal(2, calls.Length);
        Assert.Equal((range.Start, Today.AddDays(-1)), (calls[0].Start, calls[0].End));
        Assert.Equal((Today.AddDays(-1), range.End), (calls[1].Start, calls[1].End));
        Assert.All(reader.Calls, call => Assert.Equal(cts.Token, call.Token));
    }

    [Fact]
    public void HistoricalCompleteness_ClipsAtBeijingMidnightAndSkipsToday()
    {
        var reader = new FakeUsageReader { IncompleteDays = new[] { Today.AddDays(-1) } };
        var service = Service();
        var range = Range(RangeMode.Week, Today.AddDays(-3), Now);

        Assert.True(service.HasIncompleteHistoricalCache(reader, range));
        Assert.Equal(Today.AddTicks(-1), Assert.Single(reader.Calls).End);
        Assert.False(service.HasIncompleteHistoricalCache(reader, range with { Start = Today }));
        Assert.Single(reader.Calls);
    }

    [Fact]
    public void CycleResultCaching_RequiresCompleteHistoricalNonCustomRange()
    {
        var reader = new FakeUsageReader();
        var service = Service();
        var range = Range(RangeMode.Cycle, Today.AddDays(-7), Today);

        Assert.True(service.CanCacheCycleResult(reader, range, false));
        Assert.Equal(Today.AddTicks(-1), Assert.Single(reader.Calls).End);
        Assert.False(service.CanCacheCycleResult(reader, range, true));
        Assert.False(service.CanCacheCycleResult(reader, range with { End = Now }, false));
        Assert.False(service.CanCacheCycleResult(reader, range with { IsCustomStart = true }, false));
        Assert.False(service.CanCacheCycleResult(reader, range with { Mode = RangeMode.Week }, false));
        Assert.False(service.CanCacheCycleResult(reader, range with { Start = range.End }, false));
        Assert.Single(reader.Calls);
        reader.IncompleteDays = new[] { range.Start };
        Assert.False(service.CanCacheCycleResult(reader, range, false));
    }

    [Fact]
    public void Quota_UsesInjectedClockForFreshEstimateAndPassesSupplementalSnapshot()
    {
        var range = Range(RangeMode.Day, Today, Now);
        var quota = new CodexQuotaEstimate(Now.AddMinutes(-1), "codex", "Codex", null, null);
        var rows = new[] { Row(Today.AddHours(9)) };
        var reader = new FakeUsageReader { SupportsQuota = true, Summary = Summary(range, rows), Rows = rows };
        var quotaReader = new FakeQuotaReader();

        var result = Service(quotaReader).Execute(new(reader, range, true, true, quota));

        Assert.Same(quota, result.Quota);
        Assert.Equal(0, quotaReader.EstimateReads);
        Assert.Equal(rows[0].StartLocal, Assert.Single(quotaReader.Anchors));
        Assert.Equal(quota.SnapshotLocal, Assert.Single(quotaReader.Supplemental).SnapshotLocal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Quota_MissingOrStaleEstimateFallsBackToPersistedQuotaCache(bool cacheOnly)
    {
        var range = Range(RangeMode.Month, Today.AddDays(-11), Today);
        var summary = Summary(range, Row(range.Start.AddHours(9)));
        summary.DailyBuckets.Add(Row(range.Start));
        var reader = new FakeUsageReader { SupportsQuota = true, Summary = summary, Rows = new[] { Row(range.Start.AddHours(9)) } };
        var stale = new CodexQuotaEstimate(Now.AddHours(-7), null, null, null, null);
        var current = stale with { SnapshotLocal = Now };
        var quotaReader = new FakeQuotaReader { Estimate = current };

        var result = Service(quotaReader).Execute(new(reader, range, cacheOnly, false, stale));

        Assert.Same(current, result.Quota);
        Assert.Equal(1, quotaReader.EstimateReads);
        Assert.Equal(range.Start.AddDays(1).AddTicks(-1), Assert.Single(quotaReader.Anchors));
    }

    [Fact]
    public void QuotaAnchors_ExcludeRoundedWeekBucketBeforeCustomCycleBoundary()
    {
        var range = Range(RangeMode.Cycle, Today.AddDays(-2).AddMinutes(5), Today);
        var rows = new[] { Row(range.Start.AddMinutes(1)), Row(range.Start.AddMinutes(10)) };
        var reader = new FakeUsageReader { SupportsQuota = true, Summary = Summary(range, rows), Rows = rows };
        var quotaReader = new FakeQuotaReader();

        Service(quotaReader).Execute(new(reader, range, true, false, null));

        Assert.Equal(Today.AddDays(-2).AddMinutes(10), Assert.Single(quotaReader.Anchors));
    }

    [Fact]
    public void CancellationOrEmptyRange_DoesNotStartAnyReaderOperation()
    {
        var range = Range(RangeMode.Day, Today, Now);
        var reader = new FakeUsageReader();
        var service = Service();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => service.Execute(new(reader, range, false, true, null), cts.Token));
        var empty = service.Execute(new(reader, range with { End = range.Start }, false, true, null));

        Assert.Empty(reader.Calls);
        Assert.Equal(0, empty.Summary.Events);
        Assert.Empty(empty.DetailRows);
    }

    private static UsageQueryService Service(FakeQuotaReader? quotaReader = null) =>
        new(quotaReader ?? new FakeQuotaReader(), new FixedTimeProvider(Now));

    private static SelectedRange Range(RangeMode mode, DateTimeOffset start, DateTimeOffset end) =>
        new(start, end, "test range", "test details", mode);

    private static TokenUsageBucket Row(DateTimeOffset timestamp) =>
        new() { StartLocal = timestamp, LastTokenEventLocal = timestamp, Events = 1,
            InputTokens = 100, UncachedInputTokens = 100, OutputTokens = 10, TotalTokens = 110 };

    private static TokenUsageSummary Summary(SelectedRange range, params TokenUsageBucket[] rows) =>
        UsageSummaryBuilder.FromRows(range.Start, range.End, rows);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    }

    private sealed class FakeQuotaReader : IUsageQuotaReader
    {
        public CodexQuotaEstimate? Estimate { get; init; }
        public int EstimateReads { get; private set; }
        public IReadOnlyList<DateTimeOffset> Anchors { get; private set; } = Array.Empty<DateTimeOffset>();
        public IReadOnlyList<CodexQuotaSnapshot> Supplemental { get; private set; } = Array.Empty<CodexQuotaSnapshot>();
        public CodexQuotaEstimate? ReadCachedEstimate(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EstimateReads++;
            return Estimate;
        }
        public IReadOnlyList<CodexQuotaSnapshot> ReadCachedTimeline(
            IReadOnlyList<DateTimeOffset> anchors,
            IReadOnlyList<CodexQuotaSnapshot> supplementalSnapshots,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Anchors = anchors;
            Supplemental = supplementalSnapshots;
            return supplementalSnapshots;
        }
    }

    private sealed record ReaderCall(string Name, DateTimeOffset Start, DateTimeOffset End,
        bool IncludeLiveToday, CancellationToken Token);

    private sealed class FakeUsageReader : IUsageSourceReader
    {
        public UsageSource Source => UsageSource.Codex;
        public string Title => "fake";
        public bool SupportsQuota { get; init; }
        public TokenUsageSummary Summary { get; init; } = new();
        public IReadOnlyList<TokenUsageBucket> Rows { get; set; } = Array.Empty<TokenUsageBucket>();
        public IReadOnlyList<DateTimeOffset> IncompleteDays { get; set; } = Array.Empty<DateTimeOffset>();
        public Func<TokenUsageSummary>? OnRange { get; set; }
        public Func<DailyUsageSnapshot>? OnDay { get; set; }
        public Func<DateTimeOffset, DateTimeOffset, IReadOnlyList<TokenUsageBucket>>? OnDetails { get; init; }
        public Func<IReadOnlyList<TokenUsageBucket>>? OnTransient { get; init; }
        public List<ReaderCall> Calls { get; } = new();

        public TokenUsageSummary ReadCachedRange(DateTimeOffset startLocal, DateTimeOffset endLocal, CancellationToken cancellationToken = default)
        {
            Record("cached-summary", startLocal, endLocal, false, cancellationToken);
            return Summary;
        }
        public IReadOnlyList<TokenUsageBucket> ReadCachedDetailRows(DateTimeOffset startLocal, DateTimeOffset endLocal, CancellationToken cancellationToken = default)
        {
            Record("cached-details", startLocal, endLocal, false, cancellationToken);
            return Rows;
        }
        public IReadOnlyList<DateTimeOffset> GetIncompleteHistoricalDays(DateTimeOffset startInclusive, DateTimeOffset endInclusive, CancellationToken cancellationToken = default)
        {
            Record("incomplete", startInclusive, endInclusive, false, cancellationToken);
            return IncompleteDays;
        }
        public TokenUsageSummary ReadRange(DateTimeOffset startLocal, DateTimeOffset endLocal, bool includeLiveToday, CancellationToken cancellationToken = default)
        {
            Record("range", startLocal, endLocal, includeLiveToday, cancellationToken);
            return OnRange?.Invoke() ?? throw new InvalidOperationException("Unexpected source scan.");
        }
        public DailyUsageSnapshot ReadDay(DateTimeOffset startLocal, DateTimeOffset endLocal, bool includeLiveToday, CancellationToken cancellationToken = default)
        {
            Record("day", startLocal, endLocal, includeLiveToday, cancellationToken);
            return OnDay?.Invoke() ?? throw new InvalidOperationException("Unexpected day read.");
        }
        public IReadOnlyList<TokenUsageBucket> ReadDetailRows(DateTimeOffset startLocal, DateTimeOffset endLocal, bool includeLiveToday, CancellationToken cancellationToken = default)
        {
            Record("details", startLocal, endLocal, includeLiveToday, cancellationToken);
            return OnDetails?.Invoke(startLocal, endLocal) ?? throw new InvalidOperationException("Unexpected detail repair.");
        }
        public IReadOnlyList<TokenUsageBucket> ReadTransientDetailRows(DateTimeOffset startLocal, DateTimeOffset endLocal, CancellationToken cancellationToken = default)
        {
            Record("transient", startLocal, endLocal, false, cancellationToken);
            return OnTransient?.Invoke() ?? throw new InvalidOperationException("Unexpected transient source scan.");
        }
        private void Record(string name, DateTimeOffset start, DateTimeOffset end, bool live, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Calls.Add(new(name, start, end, live, token));
        }
        public bool ClearCache() => throw new InvalidOperationException("A query must not delete cache data.");
        public bool RefreshCachedDay(DateOnly date, CancellationToken cancellationToken = default) => throw new InvalidOperationException("A query must not clear a cached day.");
        public void WarmHistoricalDay(DateTimeOffset dayStart, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected warmup.");
        public void WarmHistoricalDays(IEnumerable<DateTimeOffset> daysLocal, CancellationToken cancellationToken = default, Action<DateTimeOffset>? dayCompleted = null, Action<int, int>? fileProgress = null) => throw new InvalidOperationException("Unexpected warmup.");
    }
}
