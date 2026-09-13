using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class UsagePageStateTests
{
    private static readonly DateTimeOffset Start = new(2000, 1, 1, 0, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public void RefreshingTheDisplayedHistoryEntryAlsoProtectsItFromEviction()
    {
        var page = new CodexUsageModule();
        var ranges = Enumerable.Range(0, 9).Select(Range).ToArray();
        for (var index = 0; index < 8; index++) page.CacheDisplay(ranges[index], Result(index));
        var refreshed = Result(100);

        page.StoreDisplay(ranges[0], refreshed);
        page.CacheDisplay(ranges[8], Result(8));

        Assert.False(page.TryGetCachedDisplay(ranges[1], out _));
        Assert.True(page.TryGetCachedDisplay(ranges[0], out var cached));
        Assert.Same(refreshed, cached);
        for (var index = 2; index < ranges.Length; index++)
        {
            Assert.True(page.TryGetCachedDisplay(ranges[index], out var surviving));
            Assert.Equal(index, surviving.Summary.TotalTokens);
        }
        Assert.True(page.TryGetDisplay(out var displayedRange, out var displayed));
        Assert.Same(ranges[0], displayedRange);
        Assert.Same(refreshed, displayed);
    }

    [Fact]
    public void FailedReplacementOfAnExistingEntryDoesNotPromoteOrOverwriteIt()
    {
        var page = new CodexUsageModule();
        var ranges = Enumerable.Range(0, 9).Select(Range).ToArray();
        for (var index = 0; index < 8; index++) page.CacheDisplay(ranges[index], Result(index));
        var shown = Result(70);
        page.StoreDisplay(ranges[7], shown);

        page.StoreDisplay(ranges[0], FailedResult());
        page.CacheDisplay(ranges[0], FailedResult());
        page.CacheDisplay(ranges[8], Result(8));

        // A failed refresh of the oldest entry is not a successful use of it.
        Assert.False(page.TryGetCachedDisplay(ranges[0], out _));
        Assert.True(page.TryGetCachedDisplay(ranges[1], out var untouched));
        Assert.Equal(1, untouched.Summary.TotalTokens);
        Assert.True(page.TryGetDisplay(out var displayedRange, out var displayed));
        Assert.Same(ranges[7], displayedRange);
        Assert.Same(shown, displayed);
        Assert.True(page.TryGetCachedDisplay(ranges[7], out var cached));
        Assert.Same(shown, cached);
    }

    [Fact]
    public void FailedRefreshPreservesTheSuccessfulValueForTheSameRange()
    {
        var page = new CodexUsageModule();
        var range = Range(0);
        var successful = Result(42);
        page.CacheDisplay(range, successful);
        page.StoreDisplay(range, successful);

        page.StoreDisplay(range, FailedResult());
        page.CacheDisplay(range, FailedResult());

        Assert.True(page.TryGetDisplay(out var displayedRange, out var displayed));
        Assert.Same(range, displayedRange);
        Assert.Same(successful, displayed);
        Assert.True(page.TryGetCachedDisplay(range, out var cached));
        Assert.Same(successful, cached);
    }

    [Fact]
    public void ShowingUncachedLiveRangesDoesNotEvictTheEightHistoryEntries()
    {
        var page = new CodexUsageModule();
        var history = Enumerable.Range(0, 8).Select(Range).ToArray();
        for (var index = 0; index < history.Length; index++) page.CacheDisplay(history[index], Result(index));

        SelectedRange? latest = null;
        for (var index = 8; index < 18; index++)
        {
            latest = Range(index) with { Mode = RangeMode.Day, FollowsCurrent = true };
            page.StoreDisplay(latest, Result(index));
            Assert.False(page.TryGetCachedDisplay(latest, out _));
        }

        foreach (var range in history) Assert.True(page.TryGetCachedDisplay(range, out _));
        Assert.True(page.TryGetDisplay(out var displayedRange, out var displayed));
        Assert.Same(latest, displayedRange);
        Assert.Equal(17, displayed.Summary.TotalTokens);
    }

    [Fact]
    public void SameTimeBoundsKeepDifferentBreakdownsAndCustomStartsSeparate()
    {
        var page = new CodexUsageModule();
        var ordinary = Range(0);
        var custom = ordinary with { IsCustomStart = true };
        var weekly = ordinary with { Mode = RangeMode.Week };
        page.CacheDisplay(ordinary, Result(10));
        page.CacheDisplay(custom, Result(20));
        page.CacheDisplay(weekly, Result(30));

        Assert.True(page.TryGetCachedDisplay(ordinary with
        {
            Title = "重新显示的周期", BreakdownTitle = "新的显示说明", FollowsCurrent = true
        }, out var ordinaryResult));
        Assert.Equal(10, ordinaryResult.Summary.TotalTokens);
        Assert.True(page.TryGetCachedDisplay(custom, out var customResult));
        Assert.Equal(20, customResult.Summary.TotalTokens);
        Assert.True(page.TryGetCachedDisplay(weekly, out var weeklyResult));
        Assert.Equal(30, weeklyResult.Summary.TotalTokens);
        Assert.False(page.TryGetCachedDisplay(weekly with { IsCustomStart = true }, out _));
    }

    [Fact]
    public void ClearingOneSourceKeepsItsSelectionAndEveryOtherSourceDisplay()
    {
        var pages = UsageSourceModules.Create();
        var sharedRange = Range(0);
        foreach (var (source, page) in pages)
        {
            var index = (int)source;
            page.Mode = source == UsageSource.Codex ? RangeMode.Cycle : RangeMode.Month;
            page.PickerValue = Start.Date.AddDays(index);
            page.CustomStartLocal = Start.AddHours(index);
            page.StoreDisplay(sharedRange, Result(100 + index));
            page.CacheDisplay(sharedRange, Result(100 + index));
        }

        var cleared = pages[UsageSource.ClaudeCode];
        cleared.ClearDisplay();

        Assert.False(cleared.TryGetDisplay(out _, out _));
        Assert.False(cleared.TryGetCachedDisplay(sharedRange, out _));
        Assert.Equal(RangeMode.Month, cleared.Mode);
        Assert.Equal(Start.Date.AddDays(1), cleared.PickerValue);
        Assert.Equal(Start.AddHours(1), cleared.CustomStartLocal);
        foreach (var (source, page) in pages.Where(entry => entry.Key != UsageSource.ClaudeCode))
        {
            Assert.True(page.TryGetDisplay(out var range, out var display));
            Assert.Same(sharedRange, range);
            Assert.Equal(100 + (int)source, display.Summary.TotalTokens);
            Assert.True(page.TryGetCachedDisplay(sharedRange, out var cached));
            Assert.Equal(display.Summary.TotalTokens, cached.Summary.TotalTokens);
            Assert.Equal(Start.Date.AddDays((int)source), page.PickerValue);
            Assert.Equal(Start.AddHours((int)source), page.CustomStartLocal);
        }
    }

    [Fact]
    public void FirstFailedQueryLeavesNoDisplayAndLaterSuccessCanPopulateThePage()
    {
        var page = new DshUsageModule();
        var range = Range(0) with { Mode = RangeMode.Week };
        page.StoreDisplay(range, FailedResult());
        page.CacheDisplay(range, FailedResult());
        Assert.Null(page.LastRange);
        Assert.Null(page.LastResult);
        Assert.False(page.TryGetDisplay(out _, out _));
        Assert.False(page.TryGetCachedDisplay(range, out _));

        var recovered = Result(42);
        page.StoreDisplay(range, recovered);
        page.CacheDisplay(range, recovered);
        Assert.True(page.TryGetDisplay(out var restoredRange, out var restored));
        Assert.Same(range, restoredRange);
        Assert.Same(recovered, restored);
        Assert.True(page.TryGetCachedDisplay(range, out var cached));
        Assert.Same(recovered, cached);
    }

    [Fact]
    public void PersistedCompactPageRestoresAndRefreshesWithoutRetainingLiveDetails()
    {
        using var fixture = new RestoreFixture();
        var snapshot = LastDisplayStore.Load();
        Assert.NotNull(snapshot);
        Assert.Equal(UsageSource.Codex, snapshot.Source);
        Assert.Equal(Start, snapshot.Range.Start);
        Assert.Equal(Start.AddDays(7), snapshot.Range.End);
        Assert.Equal(RangeMode.Cycle, snapshot.Range.Mode);
        Assert.True(snapshot.Range.IsCustomStart);
        Assert.True(snapshot.Range.FollowsCurrent);
        Assert.Equal(TimeSpan.FromMinutes(3), snapshot.Result.CodingTime);
        Assert.Equal(440, snapshot.Result.Summary.TotalTokens);
        Assert.Equal(40, snapshot.Result.Summary.CacheWriteInputTokens);
        Assert.Equal(440, Assert.Single(snapshot.Result.BreakdownRows).TotalTokens);
        Assert.Equal(25m, Assert.Single(snapshot.Result.QuotaSnapshots).WeekUsedPercent);
        Assert.Empty(snapshot.Result.DetailRows);

        var page = UsageSourceModules.Create()[snapshot.Source];
        page.StoreDisplay(snapshot.Range, snapshot.Result);
        page.CacheDisplay(snapshot.Range, snapshot.Result);
        Assert.True(page.TryGetDisplay(out var restoredRange, out var restored));
        Assert.Equal(snapshot.Range, restoredRange);
        Assert.Same(snapshot.Result, restored);

        var liveDetails = new[]
        {
            new TokenUsageBucket { StartLocal = Start.AddHours(9), Events = 1, TotalTokens = 220 },
            new TokenUsageBucket { StartLocal = Start.AddHours(10), Events = 1, TotalTokens = 440 }
        };
        var aggregate = new[] { new TokenUsageBucket { StartLocal = Start, Events = 2, TotalTokens = 660 } };
        var refreshed = new UsageQueryResult(
            new TokenUsageSummary { StartLocal = Start, EndLocal = Start.AddDays(7), Events = 2, TotalTokens = 660 },
            aggregate, TimeSpan.FromMinutes(8), snapshot.Result.Quota, snapshot.Result.QuotaSnapshots)
        {
            DetailRows = liveDetails
        };
        page.StoreDisplay(snapshot.Range, refreshed);

        Assert.True(page.TryGetDisplay(out _, out var displayed));
        Assert.True(page.TryGetCachedDisplay(snapshot.Range, out var cached));
        Assert.Equal(660, displayed.Summary.TotalTokens);
        Assert.Equal(TimeSpan.FromMinutes(8), displayed.CodingTime);
        Assert.Same(displayed, cached);
        Assert.Same(aggregate, displayed.BreakdownRows);
        Assert.Same(refreshed.QuotaSnapshots, displayed.QuotaSnapshots);
        Assert.Empty(displayed.DetailRows);
        Assert.Same(liveDetails, refreshed.DetailRows);
        Assert.Equal(2, refreshed.DetailRows.Count);
        Assert.Equal(440, snapshot.Result.Summary.TotalTokens);
    }

    private static SelectedRange Range(int offset)
    {
        var start = Start.AddDays(offset * 7);
        return new SelectedRange(start, start.AddDays(7), $"周期 {offset}", "周期明细", RangeMode.Cycle);
    }

    private static UsageQueryResult Result(long tokens) => new(
        new TokenUsageSummary { TotalTokens = tokens }, Array.Empty<TokenUsageBucket>(),
        TimeSpan.Zero, null, Array.Empty<CodexQuotaSnapshot>());

    private static UsageQueryResult FailedResult() => Result(0) with
    {
        CacheWarnings = new[] { new CacheWarning("isolated-cache", "read", CacheWarningKind.Corrupt, "invalid cache") }
    };

    private sealed class RestoreFixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), $"UsagePageRestoreTests-{Guid.NewGuid():N}");
        private readonly IDisposable cacheScope;
        private readonly IDisposable logScope;

        public RestoreFixture()
        {
            cacheScope = MonitorCachePaths.PushLocalAppDataRoot(root);
            logScope = UsageLogPaths.PushRoot(Path.Combine(root, "logs"));
            var folder = Path.Combine(root, "CodexTokenMonitor");
            Directory.CreateDirectory(folder);
            // This is an existing v5 on-disk document, independent of the module
            // assembly and of LastDisplayStore's process-wide pending writer.
            File.WriteAllText(Path.Combine(folder, "wpf-last-display-v5.json"), """
                {
                  "Source": 0,
                  "Range": {
                    "Start": "1999-12-31T16:00:00+00:00", "End": "2000-01-07T16:00:00+00:00",
                    "Title": "已保存的周期", "BreakdownTitle": "周期明细", "Mode": 3,
                    "IsCustomStart": true, "FollowsCurrent": true
                  },
                  "Result": {
                    "Summary": {
                      "StartLocal": "1999-12-31T16:00:00+00:00", "EndLocal": "2000-01-07T16:00:00+00:00",
                      "Events": 2, "InputTokens": 400, "CachedInputTokens": 100,
                      "CacheWriteInputTokens": 40, "UncachedInputTokens": 260,
                      "OutputTokens": 40, "TotalTokens": 440
                    },
                    "BreakdownRows": [{
                      "StartLocal": "1999-12-31T16:00:00+00:00", "Events": 2,
                      "InputTokens": 400, "CachedInputTokens": 100, "CacheWriteInputTokens": 40,
                      "UncachedInputTokens": 260, "OutputTokens": 40, "TotalTokens": 440
                    }],
                    "CodingTimeTicks": 1800000000,
                    "QuotaSnapshots": [{
                      "SnapshotLocal": "2000-01-01T01:00:00+00:00", "LimitId": "codex", "LimitName": "Codex",
                      "WeekUsedPercent": 25, "WeekResetAtLocal": "2000-01-07T16:00:00+00:00"
                    }]
                  }
                }
                """);
        }

        public void Dispose()
        {
            logScope.Dispose();
            cacheScope.Dispose();
            var resolved = Path.GetFullPath(root);
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(temp, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolved).StartsWith("UsagePageRestoreTests-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing to clean up outside the isolated page-restore test directory.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        }
    }
}
