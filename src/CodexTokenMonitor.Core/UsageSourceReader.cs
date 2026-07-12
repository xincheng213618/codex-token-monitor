namespace CodexTokenMonitor;

internal enum UsageSource
{
    Codex,
    ClaudeCode,
    ZCode,
    WorkBuddy
}

internal sealed record DailyUsageSnapshot(
    TokenUsageSummary Summary,
    IReadOnlyList<TokenUsageBucket> Rows);

internal interface IUsageSourceReader
{
    UsageSource Source { get; }
    string Title { get; }
    bool SupportsQuota { get; }

    bool ClearCache();
    bool RefreshCachedDay(DateOnly date);
    IReadOnlyList<DateTimeOffset> GetIncompleteHistoricalDays(DateTimeOffset startInclusive, DateTimeOffset endInclusive);
    TokenUsageSummary ReadRange(DateTimeOffset startLocal, DateTimeOffset endLocal, bool includeLiveToday);
    TokenUsageSummary ReadCachedRange(DateTimeOffset startLocal, DateTimeOffset endLocal);
    IReadOnlyList<TokenUsageBucket> ReadCachedDetailRows(DateTimeOffset startLocal, DateTimeOffset endLocal);
    IReadOnlyList<TokenUsageBucket> ReadDetailRows(DateTimeOffset startLocal, DateTimeOffset endLocal, bool includeLiveToday);
    IReadOnlyList<TokenUsageBucket> ReadTransientDetailRows(DateTimeOffset startLocal, DateTimeOffset endLocal);
    DailyUsageSnapshot ReadDay(DateTimeOffset startLocal, DateTimeOffset endLocal, bool includeLiveToday);
    void WarmHistoricalDay(DateTimeOffset dayStart);
}

internal static class UsageSourceReaders
{
    private static readonly IUsageSourceReader Codex = new CodexUsageSourceReader();
    private static readonly IUsageSourceReader ClaudeCode = new ClaudeCodeUsageSourceReader();
    private static readonly IUsageSourceReader ZCode = new ZCodeUsageSourceReader();
    private static readonly IUsageSourceReader WorkBuddy = new WorkBuddyUsageSourceReader();

    public static IReadOnlyList<IUsageSourceReader> All { get; } = new[]
    {
        Codex,
        ClaudeCode,
        ZCode,
        WorkBuddy
    };

    public static IUsageSourceReader For(UsageSource source)
    {
        return source switch
        {
            UsageSource.ClaudeCode => ClaudeCode,
            UsageSource.ZCode => ZCode,
            UsageSource.WorkBuddy => WorkBuddy,
            _ => Codex
        };
    }

    private sealed class CodexUsageSourceReader : IUsageSourceReader
    {
        public UsageSource Source => UsageSource.Codex;
        public string Title => "Codex";
        public bool SupportsQuota => true;

        public bool ClearCache()
        {
            return CodexUsageReader.ClearCache();
        }

        public bool RefreshCachedDay(DateOnly date)
        {
            var deleted = CodexUsageReader.ClearCachedDay(date);
            var dayStart = StartOfDay(date);
            _ = CodexUsageReader.ReadDetailRows(dayStart, EndForRefresh(dayStart), includeLiveToday: true);
            CodexUsageReader.WarmQuotaSnapshotDay(dayStart);
            CodexUsageReader.WarmQuotaTimelineDay(dayStart);
            return deleted;
        }

        public IReadOnlyList<DateTimeOffset> GetIncompleteHistoricalDays(
            DateTimeOffset startInclusive,
            DateTimeOffset endInclusive)
        {
            return CodexUsageReader.GetIncompleteHistoricalDays(startInclusive, endInclusive);
        }

        public TokenUsageSummary ReadRange(
            DateTimeOffset startLocal,
            DateTimeOffset endLocal,
            bool includeLiveToday)
        {
            return CodexUsageReader.ReadRange(startLocal, endLocal, includeLiveToday);
        }

        public TokenUsageSummary ReadCachedRange(DateTimeOffset startLocal, DateTimeOffset endLocal)
        {
            return CodexUsageReader.ReadCachedRange(startLocal, endLocal);
        }

        public IReadOnlyList<TokenUsageBucket> ReadCachedDetailRows(DateTimeOffset startLocal, DateTimeOffset endLocal)
        {
            return CodexUsageReader.ReadCachedDetailRows(startLocal, endLocal);
        }

        public IReadOnlyList<TokenUsageBucket> ReadDetailRows(
            DateTimeOffset startLocal,
            DateTimeOffset endLocal,
            bool includeLiveToday)
        {
            return CodexUsageReader.ReadDetailRows(startLocal, endLocal, includeLiveToday);
        }

        public IReadOnlyList<TokenUsageBucket> ReadTransientDetailRows(DateTimeOffset startLocal, DateTimeOffset endLocal)
        {
            return CodexUsageReader.ReadTransientDetailRows(startLocal, endLocal);
        }

        public DailyUsageSnapshot ReadDay(DateTimeOffset startLocal, DateTimeOffset endLocal, bool includeLiveToday)
        {
            var rows = includeLiveToday
                ? CodexUsageReader.ReadDetailRows(startLocal, endLocal, includeLiveToday)
                : CodexUsageReader.ReadCachedDetailRows(startLocal, endLocal);
            var summary = includeLiveToday
                ? CreateSummaryFromRows(startLocal, endLocal, rows)
                : CodexUsageReader.ReadCachedRange(startLocal, endLocal);
            return new DailyUsageSnapshot(summary, rows);
        }

        public void WarmHistoricalDay(DateTimeOffset dayStart)
        {
            var dayEnd = dayStart.AddDays(1);
            _ = CodexUsageReader.ReadRange(dayStart, dayEnd, includeLiveToday: false);
            _ = CodexUsageReader.ReadDetailRows(dayStart, dayEnd, includeLiveToday: false);
            CodexUsageReader.WarmQuotaTimelineDay(dayStart);
        }
    }

    private sealed class ClaudeCodeUsageSourceReader : IUsageSourceReader
    {
        public UsageSource Source => UsageSource.ClaudeCode;
        public string Title => "Claude Code";
        public bool SupportsQuota => false;

        public bool ClearCache()
        {
            return ClaudeUsageReader.ClearCache();
        }

        public bool RefreshCachedDay(DateOnly date)
        {
            var deleted = ClaudeUsageReader.ClearCachedDay(date);
            var dayStart = StartOfDay(date);
            _ = ClaudeUsageReader.ReadDetailRows(dayStart, EndForRefresh(dayStart), includeLiveToday: true);
            return deleted;
        }

        public IReadOnlyList<DateTimeOffset> GetIncompleteHistoricalDays(
            DateTimeOffset startInclusive,
            DateTimeOffset endInclusive)
        {
            return ClaudeUsageReader.GetIncompleteHistoricalDays(startInclusive, endInclusive);
        }

        public TokenUsageSummary ReadRange(
            DateTimeOffset startLocal,
            DateTimeOffset endLocal,
            bool includeLiveToday)
        {
            return ClaudeUsageReader.ReadRange(startLocal, endLocal, includeLiveToday);
        }

        public TokenUsageSummary ReadCachedRange(DateTimeOffset startLocal, DateTimeOffset endLocal)
        {
            return ClaudeUsageReader.ReadCachedRange(startLocal, endLocal);
        }

        public IReadOnlyList<TokenUsageBucket> ReadCachedDetailRows(DateTimeOffset startLocal, DateTimeOffset endLocal)
        {
            return ClaudeUsageReader.ReadCachedDetailRows(startLocal, endLocal);
        }

        public IReadOnlyList<TokenUsageBucket> ReadDetailRows(
            DateTimeOffset startLocal,
            DateTimeOffset endLocal,
            bool includeLiveToday)
        {
            return ClaudeUsageReader.ReadDetailRows(startLocal, endLocal, includeLiveToday);
        }

        public IReadOnlyList<TokenUsageBucket> ReadTransientDetailRows(DateTimeOffset startLocal, DateTimeOffset endLocal)
        {
            return ClaudeUsageReader.ReadTransientDetailRows(startLocal, endLocal);
        }

        public DailyUsageSnapshot ReadDay(DateTimeOffset startLocal, DateTimeOffset endLocal, bool includeLiveToday)
        {
            var rows = includeLiveToday
                ? ClaudeUsageReader.ReadDetailRows(startLocal, endLocal, includeLiveToday)
                : ClaudeUsageReader.ReadCachedDetailRows(startLocal, endLocal);
            var summary = includeLiveToday
                ? CreateSummaryFromRows(startLocal, endLocal, rows)
                : ClaudeUsageReader.ReadCachedRange(startLocal, endLocal);
            return new DailyUsageSnapshot(summary, rows);
        }

        public void WarmHistoricalDay(DateTimeOffset dayStart)
        {
            var dayEnd = dayStart.AddDays(1);
            _ = ClaudeUsageReader.ReadRange(dayStart, dayEnd, includeLiveToday: false);
            _ = ClaudeUsageReader.ReadDetailRows(dayStart, dayEnd, includeLiveToday: false);
        }
    }

    private sealed class ZCodeUsageSourceReader : IUsageSourceReader
    {
        public UsageSource Source => UsageSource.ZCode;
        public string Title => "ZCode";
        public bool SupportsQuota => false;

        public bool ClearCache()
        {
            return ZCodeUsageReader.ClearCache();
        }

        public bool RefreshCachedDay(DateOnly date)
        {
            var deleted = ZCodeUsageReader.ClearCachedDay(date);
            var dayStart = StartOfDay(date);
            _ = ZCodeUsageReader.ReadDetailRows(dayStart, EndForRefresh(dayStart), includeLiveToday: true);
            return deleted;
        }

        public IReadOnlyList<DateTimeOffset> GetIncompleteHistoricalDays(
            DateTimeOffset startInclusive,
            DateTimeOffset endInclusive)
        {
            return ZCodeUsageReader.GetIncompleteHistoricalDays(startInclusive, endInclusive);
        }

        public TokenUsageSummary ReadRange(
            DateTimeOffset startLocal,
            DateTimeOffset endLocal,
            bool includeLiveToday)
        {
            return ZCodeUsageReader.ReadRange(startLocal, endLocal, includeLiveToday);
        }

        public TokenUsageSummary ReadCachedRange(DateTimeOffset startLocal, DateTimeOffset endLocal)
        {
            return ZCodeUsageReader.ReadCachedRange(startLocal, endLocal);
        }

        public IReadOnlyList<TokenUsageBucket> ReadCachedDetailRows(DateTimeOffset startLocal, DateTimeOffset endLocal)
        {
            return ZCodeUsageReader.ReadCachedDetailRows(startLocal, endLocal);
        }

        public IReadOnlyList<TokenUsageBucket> ReadDetailRows(
            DateTimeOffset startLocal,
            DateTimeOffset endLocal,
            bool includeLiveToday)
        {
            return ZCodeUsageReader.ReadDetailRows(startLocal, endLocal, includeLiveToday);
        }

        public IReadOnlyList<TokenUsageBucket> ReadTransientDetailRows(DateTimeOffset startLocal, DateTimeOffset endLocal)
        {
            return ZCodeUsageReader.ReadTransientDetailRows(startLocal, endLocal);
        }

        public DailyUsageSnapshot ReadDay(DateTimeOffset startLocal, DateTimeOffset endLocal, bool includeLiveToday)
        {
            var rows = includeLiveToday
                ? ZCodeUsageReader.ReadDetailRows(startLocal, endLocal, includeLiveToday)
                : ZCodeUsageReader.ReadCachedDetailRows(startLocal, endLocal);
            var summary = includeLiveToday
                ? CreateSummaryFromRows(startLocal, endLocal, rows)
                : ZCodeUsageReader.ReadCachedRange(startLocal, endLocal);
            return new DailyUsageSnapshot(summary, rows);
        }

        public void WarmHistoricalDay(DateTimeOffset dayStart)
        {
            var dayEnd = dayStart.AddDays(1);
            _ = ZCodeUsageReader.ReadRange(dayStart, dayEnd, includeLiveToday: false);
            _ = ZCodeUsageReader.ReadDetailRows(dayStart, dayEnd, includeLiveToday: false);
        }
    }

    private sealed class WorkBuddyUsageSourceReader : IUsageSourceReader
    {
        public UsageSource Source => UsageSource.WorkBuddy;
        public string Title => "WorkBuddy";
        public bool SupportsQuota => false;

        public bool ClearCache()
        {
            return WorkBuddyUsageReader.ClearCache();
        }

        public bool RefreshCachedDay(DateOnly date)
        {
            var deleted = WorkBuddyUsageReader.ClearCachedDay(date);
            var dayStart = StartOfDay(date);
            _ = WorkBuddyUsageReader.ReadDetailRows(dayStart, EndForRefresh(dayStart), includeLiveToday: true);
            return deleted;
        }

        public IReadOnlyList<DateTimeOffset> GetIncompleteHistoricalDays(
            DateTimeOffset startInclusive,
            DateTimeOffset endInclusive)
        {
            return WorkBuddyUsageReader.GetIncompleteHistoricalDays(startInclusive, endInclusive);
        }

        public TokenUsageSummary ReadRange(
            DateTimeOffset startLocal,
            DateTimeOffset endLocal,
            bool includeLiveToday)
        {
            return WorkBuddyUsageReader.ReadRange(startLocal, endLocal, includeLiveToday);
        }

        public TokenUsageSummary ReadCachedRange(DateTimeOffset startLocal, DateTimeOffset endLocal)
        {
            return WorkBuddyUsageReader.ReadCachedRange(startLocal, endLocal);
        }

        public IReadOnlyList<TokenUsageBucket> ReadCachedDetailRows(DateTimeOffset startLocal, DateTimeOffset endLocal)
        {
            return WorkBuddyUsageReader.ReadCachedDetailRows(startLocal, endLocal);
        }

        public IReadOnlyList<TokenUsageBucket> ReadDetailRows(
            DateTimeOffset startLocal,
            DateTimeOffset endLocal,
            bool includeLiveToday)
        {
            return WorkBuddyUsageReader.ReadDetailRows(startLocal, endLocal, includeLiveToday);
        }

        public IReadOnlyList<TokenUsageBucket> ReadTransientDetailRows(DateTimeOffset startLocal, DateTimeOffset endLocal)
        {
            return WorkBuddyUsageReader.ReadTransientDetailRows(startLocal, endLocal);
        }

        public DailyUsageSnapshot ReadDay(DateTimeOffset startLocal, DateTimeOffset endLocal, bool includeLiveToday)
        {
            var rows = includeLiveToday
                ? WorkBuddyUsageReader.ReadDetailRows(startLocal, endLocal, includeLiveToday)
                : WorkBuddyUsageReader.ReadCachedDetailRows(startLocal, endLocal);
            var summary = includeLiveToday
                ? CreateSummaryFromRows(startLocal, endLocal, rows)
                : WorkBuddyUsageReader.ReadCachedRange(startLocal, endLocal);
            return new DailyUsageSnapshot(summary, rows);
        }

        public void WarmHistoricalDay(DateTimeOffset dayStart)
        {
            var dayEnd = dayStart.AddDays(1);
            _ = WorkBuddyUsageReader.ReadRange(dayStart, dayEnd, includeLiveToday: false);
            _ = WorkBuddyUsageReader.ReadDetailRows(dayStart, dayEnd, includeLiveToday: false);
        }
    }

    private static DateTimeOffset StartOfDay(DateOnly date)
    {
        return new DateTimeOffset(date.Year, date.Month, date.Day, 0, 0, 0, CodexUsageReader.BeijingOffset);
    }

    private static DateTimeOffset EndForRefresh(DateTimeOffset dayStart)
    {
        var dayEnd = dayStart.AddDays(1);
        var now = DateTimeOffset.UtcNow.ToOffset(CodexUsageReader.BeijingOffset);
        return dayStart <= now && dayEnd > now ? now : dayEnd;
    }

    private static TokenUsageSummary CreateSummaryFromRows(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        IReadOnlyList<TokenUsageBucket> rows)
    {
        var summary = new TokenUsageSummary
        {
            StartLocal = startLocal,
            EndLocal = endLocal
        };

        foreach (var row in rows)
        {
            summary.Add(
                row.StartLocal,
                row.InputTokens,
                row.CachedInputTokens,
                row.OutputTokens,
                row.ReasoningOutputTokens,
                row.TotalTokens);
        }

        return summary;
    }
}
