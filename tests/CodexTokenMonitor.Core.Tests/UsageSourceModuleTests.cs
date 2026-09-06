using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class UsageSourceModuleTests
{
    [Fact]
    public void CachedDisplay_ReusesExactRangeAndClearInvalidatesIt()
    {
        var module = new CodexUsageModule();
        var range = CycleRange(0);
        var result = Result(42);

        module.CacheDisplay(range, result);

        Assert.True(module.TryGetCachedDisplay(range, out var cached));
        Assert.Same(result, cached);
        Assert.False(module.TryGetCachedDisplay(range with { End = range.End.AddMinutes(1) }, out _));

        module.ClearDisplay();

        Assert.False(module.TryGetCachedDisplay(range, out _));
    }

    [Fact]
    public void CachedDisplay_UsesLeastRecentlyUsedEviction()
    {
        var module = new CodexUsageModule();
        var ranges = Enumerable.Range(0, 9).Select(CycleRange).ToList();
        for (var index = 0; index < 8; index++)
        {
            module.CacheDisplay(ranges[index], Result(index));
        }

        Assert.True(module.TryGetCachedDisplay(ranges[0], out _));
        module.CacheDisplay(ranges[8], Result(8));

        Assert.True(module.TryGetCachedDisplay(ranges[0], out _));
        Assert.False(module.TryGetCachedDisplay(ranges[1], out _));
        Assert.True(module.TryGetCachedDisplay(ranges[8], out _));
    }

    [Fact]
    public void StoreDisplay_RefreshesAnExistingCachedResult()
    {
        var module = new CodexUsageModule();
        var range = CycleRange(0);
        module.CacheDisplay(range, Result(1));
        var updated = Result(2);

        module.StoreDisplay(range, updated);

        Assert.True(module.TryGetCachedDisplay(range, out var cached));
        Assert.Same(updated, cached);
    }

    [Fact]
    public void DisplayStorage_DoesNotRetainLargeDetailRows()
    {
        var module = new CodexUsageModule();
        var range = CycleRange(0);
        var result = Result(42) with
        {
            DetailRows = new[]
            {
                new TokenUsageBucket
                {
                    StartLocal = range.Start,
                    Events = 1,
                    TotalTokens = 42
                }
            }
        };

        module.StoreDisplay(range, result);
        module.CacheDisplay(range, result);

        Assert.True(module.TryGetDisplay(out _, out var display));
        Assert.Empty(display.DetailRows);
        Assert.True(module.TryGetCachedDisplay(range, out var cached));
        Assert.Empty(cached.DetailRows);
        Assert.Same(result.BreakdownRows, display.BreakdownRows);
        Assert.NotSame(result, display);
        Assert.NotSame(result, cached);
    }

    private static SelectedRange CycleRange(int weekOffset)
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(8)).AddDays(weekOffset * 7);
        return new SelectedRange(start, start.AddDays(7), $"cycle-{weekOffset}", "details", RangeMode.Cycle);
    }

    private static UsageQueryResult Result(long totalTokens)
    {
        return new UsageQueryResult(
            new TokenUsageSummary { TotalTokens = totalTokens },
            Array.Empty<TokenUsageBucket>(),
            TimeSpan.Zero,
            null,
            Array.Empty<CodexQuotaSnapshot>());
    }
}
