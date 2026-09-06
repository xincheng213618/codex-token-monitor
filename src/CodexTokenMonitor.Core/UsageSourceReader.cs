namespace CodexTokenMonitor;

internal enum UsageSource
{
    Codex,
    ClaudeCode,
    ZCode,
    WorkBuddy,
    Dsh
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
    bool RefreshCachedDay(DateOnly date, CancellationToken cancellationToken = default);
    IReadOnlyList<DateTimeOffset> GetIncompleteHistoricalDays(
        DateTimeOffset startInclusive,
        DateTimeOffset endInclusive,
        CancellationToken cancellationToken = default);
    TokenUsageSummary ReadRange(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        bool includeLiveToday,
        CancellationToken cancellationToken = default);
    TokenUsageSummary ReadCachedRange(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        CancellationToken cancellationToken = default);
    IReadOnlyList<TokenUsageBucket> ReadCachedDetailRows(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        CancellationToken cancellationToken = default);
    IReadOnlyList<TokenUsageBucket> ReadDetailRows(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        bool includeLiveToday,
        CancellationToken cancellationToken = default);
    IReadOnlyList<TokenUsageBucket> ReadTransientDetailRows(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        CancellationToken cancellationToken = default);
    DailyUsageSnapshot ReadDay(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        bool includeLiveToday,
        CancellationToken cancellationToken = default);
    void WarmHistoricalDay(DateTimeOffset dayStart, CancellationToken cancellationToken = default);
    void WarmHistoricalDays(
        IEnumerable<DateTimeOffset> daysLocal,
        CancellationToken cancellationToken = default,
        Action<DateTimeOffset>? dayCompleted = null,
        Action<int, int>? fileProgress = null);
}

internal static class UsageSourceReaders
{
    private static readonly IUsageSourceReader Codex = new CodexUsageSourceReader();
    private static readonly IUsageSourceReader ClaudeCode = new ClaudeCodeUsageSourceReader();
    private static readonly IUsageSourceReader ZCode = new ZCodeUsageSourceReader();
    private static readonly IUsageSourceReader WorkBuddy = new WorkBuddyUsageSourceReader();
    private static readonly IUsageSourceReader Dsh = new DshUsageSourceReader();

    public static IReadOnlyList<IUsageSourceReader> All { get; } = new[]
    {
        Codex,
        ClaudeCode,
        ZCode,
        WorkBuddy,
        Dsh
    };

    public static IUsageSourceReader For(UsageSource source)
    {
        return source switch
        {
            UsageSource.ClaudeCode => ClaudeCode,
            UsageSource.ZCode => ZCode,
            UsageSource.WorkBuddy => WorkBuddy,
            UsageSource.Dsh => Dsh,
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

        public bool RefreshCachedDay(DateOnly date, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var deleted = CodexUsageReader.ClearCachedDay(date);
            var dayStart = StartOfDay(date);
            _ = CodexUsageReader.ReadDetailRows(
                dayStart,
                EndForRefresh(dayStart),
                includeLiveToday: true,
                cancellationToken: cancellationToken);
            CodexUsageReader.WarmQuotaSnapshotDay(dayStart, cancellationToken);
            CodexUsageReader.WarmQuotaTimelineDay(dayStart, cancellationToken);
            return deleted;
        }

        public IReadOnlyList<DateTimeOffset> GetIncompleteHistoricalDays(
            DateTimeOffset startInclusive,
            DateTimeOffset endInclusive,
            CancellationToken cancellationToken = default)
        {
            return CodexUsageReader.GetIncompleteHistoricalDays(startInclusive, endInclusive, cancellationToken);
        }

        public TokenUsageSummary ReadRange(
            DateTimeOffset startLocal,
            DateTimeOffset endLocal,
            bool includeLiveToday,
            CancellationToken cancellationToken = default)
        {
            return CodexUsageReader.ReadRange(startLocal, endLocal, includeLiveToday, cancellationToken);
        }

        public TokenUsageSummary ReadCachedRange(
            DateTimeOffset startLocal,
            DateTimeOffset endLocal,
            CancellationToken cancellationToken = default)
        {
            return CodexUsageReader.ReadCachedRange(startLocal, endLocal, cancellationToken);
        }

        public IReadOnlyList<TokenUsageBucket> ReadCachedDetailRows(
            DateTimeOffset startLocal,
            DateTimeOffset endLocal,
            CancellationToken cancellationToken = default)
        {
            return CodexUsageReader.ReadCachedDetailRows(startLocal, endLocal, cancellationToken);
        }

        public IReadOnlyList<TokenUsageBucket> ReadDetailRows(
            DateTimeOffset startLocal,
            DateTimeOffset endLocal,
            bool includeLiveToday,
            CancellationToken cancellationToken = default)
        {
            return CodexUsageReader.ReadDetailRows(startLocal, endLocal, includeLiveToday, cancellationToken);
        }

        public IReadOnlyList<TokenUsageBucket> ReadTransientDetailRows(
            DateTimeOffset startLocal,
            DateTimeOffset endLocal,
            CancellationToken cancellationToken = default)
        {
            return CodexUsageReader.ReadTransientDetailRows(startLocal, endLocal, cancellationToken);
        }

        public DailyUsageSnapshot ReadDay(
            DateTimeOffset startLocal,
            DateTimeOffset endLocal,
            bool includeLiveToday,
            CancellationToken cancellationToken = default)
        {
            var rows = includeLiveToday
                ? CodexUsageReader.ReadDetailRows(startLocal, endLocal, includeLiveToday, cancellationToken)
                : CodexUsageReader.ReadCachedDetailRows(startLocal, endLocal, cancellationToken);
            var summary = includeLiveToday
                ? CreateSummaryFromRows(startLocal, endLocal, rows)
                : CodexUsageReader.ReadCachedRange(startLocal, endLocal, cancellationToken);
            return new DailyUsageSnapshot(summary, rows);
        }

        public void WarmHistoricalDay(DateTimeOffset dayStart, CancellationToken cancellationToken = default)
        {
            WarmHistoricalDays(new[] { dayStart }, cancellationToken);
        }

        public void WarmHistoricalDays(
            IEnumerable<DateTimeOffset> daysLocal,
            CancellationToken cancellationToken = default,
            Action<DateTimeOffset>? dayCompleted = null,
            Action<int, int>? fileProgress = null)
        {
            CodexUsageReader.WarmHistoricalDays(daysLocal, cancellationToken, dayCompleted, fileProgress);
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

        public bool RefreshCachedDay(DateOnly date, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var deleted = ClaudeUsageReader.ClearCachedDay(date);
            var dayStart = StartOfDay(date);
            _ = ClaudeUsageReader.ReadDetailRows(
                dayStart,
                EndForRefresh(dayStart),
                includeLiveToday: true,
                cancellationToken: cancellationToken);
            return deleted;
        }

        public IReadOnlyList<DateTimeOffset> GetIncompleteHistoricalDays(
            DateTimeOffset startInclusive,
            DateTimeOffset endInclusive,
            CancellationToken cancellationToken = default)
        {
            return ClaudeUsageReader.GetIncompleteHistoricalDays(startInclusive, endInclusive, cancellationToken);
        }

        public TokenUsageSummary ReadRange(
            DateTimeOffset startLocal,
            DateTimeOffset endLocal,
            bool includeLiveToday,
            CancellationToken cancellationToken = default)
        {
            return ClaudeUsageReader.ReadRange(startLocal, endLocal, includeLiveToday, cancellationToken);
        }

        public TokenUsageSummary ReadCachedRange(
            DateTimeOffset startLocal,
            DateTimeOffset endLocal,
            CancellationToken cancellationToken = default)
        {
            return ClaudeUsageReader.ReadCachedRange(startLocal, endLocal, cancellationToken);
        }

        public IReadOnlyList<TokenUsageBucket> ReadCachedDetailRows(
            DateTimeOffset startLocal,
            DateTimeOffset endLocal,
            CancellationToken cancellationToken = default)
        {
            return ClaudeUsageReader.ReadCachedDetailRows(startLocal, endLocal, cancellationToken);
        }

        public IReadOnlyList<TokenUsageBucket> ReadDetailRows(
            DateTimeOffset startLocal,
            DateTimeOffset endLocal,
            bool includeLiveToday,
            CancellationToken cancellationToken = default)
        {
            return ClaudeUsageReader.ReadDetailRows(startLocal, endLocal, includeLiveToday, cancellationToken);
        }

        public IReadOnlyList<TokenUsageBucket> ReadTransientDetailRows(
            DateTimeOffset startLocal,
            DateTimeOffset endLocal,
            CancellationToken cancellationToken = default)
        {
            return ClaudeUsageReader.ReadTransientDetailRows(startLocal, endLocal, cancellationToken);
        }

        public DailyUsageSnapshot ReadDay(
            DateTimeOffset startLocal,
            DateTimeOffset endLocal,
            bool includeLiveToday,
            CancellationToken cancellationToken = default)
        {
            var rows = includeLiveToday
                ? ClaudeUsageReader.ReadDetailRows(startLocal, endLocal, includeLiveToday, cancellationToken)
                : ClaudeUsageReader.ReadCachedDetailRows(startLocal, endLocal, cancellationToken);
            var summary = includeLiveToday
                ? CreateSummaryFromRows(startLocal, endLocal, rows)
                : ClaudeUsageReader.ReadCachedRange(startLocal, endLocal, cancellationToken);
            return new DailyUsageSnapshot(summary, rows);
        }

        public void WarmHistoricalDay(DateTimeOffset dayStart, CancellationToken cancellationToken = default)
        {
            WarmHistoricalDays(new[] { dayStart }, cancellationToken);
        }

        public void WarmHistoricalDays(
            IEnumerable<DateTimeOffset> daysLocal,
            CancellationToken cancellationToken = default,
            Action<DateTimeOffset>? dayCompleted = null,
            Action<int, int>? fileProgress = null)
        {
            ClaudeUsageReader.WarmHistoricalDays(daysLocal, cancellationToken, dayCompleted, fileProgress);
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

        public bool RefreshCachedDay(DateOnly date, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var deleted = ZCodeUsageReader.ClearCachedDay(date);
            var dayStart = StartOfDay(date);
            _ = ZCodeUsageReader.ReadDetailRows(
                dayStart,
                EndForRefresh(dayStart),
                includeLiveToday: true,
                cancellationToken: cancellationToken);
            return deleted;
        }

        public IReadOnlyList<DateTimeOffset> GetIncompleteHistoricalDays(
            DateTimeOffset startInclusive,
            DateTimeOffset endInclusive,
            CancellationToken cancellationToken = default)
        {
            return ZCodeUsageReader.GetIncompleteHistoricalDays(startInclusive, endInclusive, cancellationToken);
        }

        public TokenUsageSummary ReadRange(
            DateTimeOffset startLocal,
            DateTimeOffset endLocal,
            bool includeLiveToday,
            CancellationToken cancellationToken = default)
        {
            return ZCodeUsageReader.ReadRange(startLocal, endLocal, includeLiveToday, cancellationToken);
        }

        public TokenUsageSummary ReadCachedRange(
            DateTimeOffset startLocal,
            DateTimeOffset endLocal,
            CancellationToken cancellationToken = default)
        {
            return ZCodeUsageReader.ReadCachedRange(startLocal, endLocal, cancellationToken);
        }

        public IReadOnlyList<TokenUsageBucket> ReadCachedDetailRows(
            DateTimeOffset startLocal,
            DateTimeOffset endLocal,
            CancellationToken cancellationToken = default)
        {
            return ZCodeUsageReader.ReadCachedDetailRows(startLocal, endLocal, cancellationToken);
        }

        public IReadOnlyList<TokenUsageBucket> ReadDetailRows(
            DateTimeOffset startLocal,
            DateTimeOffset endLocal,
            bool includeLiveToday,
            CancellationToken cancellationToken = default)
        {
            return ZCodeUsageReader.ReadDetailRows(startLocal, endLocal, includeLiveToday, cancellationToken);
        }

        public IReadOnlyList<TokenUsageBucket> ReadTransientDetailRows(
            DateTimeOffset startLocal,
            DateTimeOffset endLocal,
            CancellationToken cancellationToken = default)
        {
            return ZCodeUsageReader.ReadTransientDetailRows(startLocal, endLocal, cancellationToken);
        }

        public DailyUsageSnapshot ReadDay(
            DateTimeOffset startLocal,
            DateTimeOffset endLocal,
            bool includeLiveToday,
            CancellationToken cancellationToken = default)
        {
            var rows = includeLiveToday
                ? ZCodeUsageReader.ReadDetailRows(startLocal, endLocal, includeLiveToday, cancellationToken)
                : ZCodeUsageReader.ReadCachedDetailRows(startLocal, endLocal, cancellationToken);
            var summary = includeLiveToday
                ? CreateSummaryFromRows(startLocal, endLocal, rows)
                : ZCodeUsageReader.ReadCachedRange(startLocal, endLocal, cancellationToken);
            return new DailyUsageSnapshot(summary, rows);
        }

        public void WarmHistoricalDay(DateTimeOffset dayStart, CancellationToken cancellationToken = default)
        {
            WarmHistoricalDays(new[] { dayStart }, cancellationToken);
        }

        public void WarmHistoricalDays(
            IEnumerable<DateTimeOffset> daysLocal,
            CancellationToken cancellationToken = default,
            Action<DateTimeOffset>? dayCompleted = null,
            Action<int, int>? fileProgress = null)
        {
            ZCodeUsageReader.WarmHistoricalDays(daysLocal, cancellationToken, dayCompleted, fileProgress);
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

        public bool RefreshCachedDay(DateOnly date, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var deleted = WorkBuddyUsageReader.ClearCachedDay(date);
            var dayStart = StartOfDay(date);
            _ = WorkBuddyUsageReader.ReadDetailRows(
                dayStart,
                EndForRefresh(dayStart),
                includeLiveToday: true,
                cancellationToken: cancellationToken);
            return deleted;
        }

        public IReadOnlyList<DateTimeOffset> GetIncompleteHistoricalDays(
            DateTimeOffset startInclusive,
            DateTimeOffset endInclusive,
            CancellationToken cancellationToken = default)
        {
            return WorkBuddyUsageReader.GetIncompleteHistoricalDays(startInclusive, endInclusive, cancellationToken);
        }

        public TokenUsageSummary ReadRange(
            DateTimeOffset startLocal,
            DateTimeOffset endLocal,
            bool includeLiveToday,
            CancellationToken cancellationToken = default)
        {
            return WorkBuddyUsageReader.ReadRange(startLocal, endLocal, includeLiveToday, cancellationToken);
        }

        public TokenUsageSummary ReadCachedRange(
            DateTimeOffset startLocal,
            DateTimeOffset endLocal,
            CancellationToken cancellationToken = default)
        {
            return WorkBuddyUsageReader.ReadCachedRange(startLocal, endLocal, cancellationToken);
        }

        public IReadOnlyList<TokenUsageBucket> ReadCachedDetailRows(
            DateTimeOffset startLocal,
            DateTimeOffset endLocal,
            CancellationToken cancellationToken = default)
        {
            return WorkBuddyUsageReader.ReadCachedDetailRows(startLocal, endLocal, cancellationToken);
        }

        public IReadOnlyList<TokenUsageBucket> ReadDetailRows(
            DateTimeOffset startLocal,
            DateTimeOffset endLocal,
            bool includeLiveToday,
            CancellationToken cancellationToken = default)
        {
            return WorkBuddyUsageReader.ReadDetailRows(startLocal, endLocal, includeLiveToday, cancellationToken);
        }

        public IReadOnlyList<TokenUsageBucket> ReadTransientDetailRows(
            DateTimeOffset startLocal,
            DateTimeOffset endLocal,
            CancellationToken cancellationToken = default)
        {
            return WorkBuddyUsageReader.ReadTransientDetailRows(startLocal, endLocal, cancellationToken);
        }

        public DailyUsageSnapshot ReadDay(
            DateTimeOffset startLocal,
            DateTimeOffset endLocal,
            bool includeLiveToday,
            CancellationToken cancellationToken = default)
        {
            var rows = includeLiveToday
                ? WorkBuddyUsageReader.ReadDetailRows(startLocal, endLocal, includeLiveToday, cancellationToken)
                : WorkBuddyUsageReader.ReadCachedDetailRows(startLocal, endLocal, cancellationToken);
            var summary = includeLiveToday
                ? CreateSummaryFromRows(startLocal, endLocal, rows)
                : WorkBuddyUsageReader.ReadCachedRange(startLocal, endLocal, cancellationToken);
            return new DailyUsageSnapshot(summary, rows);
        }

        public void WarmHistoricalDay(DateTimeOffset dayStart, CancellationToken cancellationToken = default)
        {
            WarmHistoricalDays(new[] { dayStart }, cancellationToken);
        }

        public void WarmHistoricalDays(
            IEnumerable<DateTimeOffset> daysLocal,
            CancellationToken cancellationToken = default,
            Action<DateTimeOffset>? dayCompleted = null,
            Action<int, int>? fileProgress = null)
        {
            WorkBuddyUsageReader.WarmHistoricalDays(daysLocal, cancellationToken, dayCompleted, fileProgress);
        }
    }

    private sealed class DshUsageSourceReader : IUsageSourceReader
    {
        public UsageSource Source => UsageSource.Dsh;
        public string Title => "DSH";
        public bool SupportsQuota => false;

        public bool ClearCache()
        {
            return DshUsageReader.ClearCache();
        }

        public bool RefreshCachedDay(DateOnly date, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var deleted = DshUsageReader.ClearCachedDay(date);
            var dayStart = StartOfDay(date);
            _ = DshUsageReader.ReadDetailRows(
                dayStart,
                EndForRefresh(dayStart),
                includeLiveToday: true,
                cancellationToken: cancellationToken);
            return deleted;
        }

        public IReadOnlyList<DateTimeOffset> GetIncompleteHistoricalDays(
            DateTimeOffset startInclusive,
            DateTimeOffset endInclusive,
            CancellationToken cancellationToken = default)
        {
            return DshUsageReader.GetIncompleteHistoricalDays(startInclusive, endInclusive, cancellationToken);
        }

        public TokenUsageSummary ReadRange(
            DateTimeOffset startLocal,
            DateTimeOffset endLocal,
            bool includeLiveToday,
            CancellationToken cancellationToken = default)
        {
            return DshUsageReader.ReadRange(startLocal, endLocal, includeLiveToday, cancellationToken);
        }

        public TokenUsageSummary ReadCachedRange(
            DateTimeOffset startLocal,
            DateTimeOffset endLocal,
            CancellationToken cancellationToken = default)
        {
            return DshUsageReader.ReadCachedRange(startLocal, endLocal, cancellationToken);
        }

        public IReadOnlyList<TokenUsageBucket> ReadCachedDetailRows(
            DateTimeOffset startLocal,
            DateTimeOffset endLocal,
            CancellationToken cancellationToken = default)
        {
            return DshUsageReader.ReadCachedDetailRows(startLocal, endLocal, cancellationToken);
        }

        public IReadOnlyList<TokenUsageBucket> ReadDetailRows(
            DateTimeOffset startLocal,
            DateTimeOffset endLocal,
            bool includeLiveToday,
            CancellationToken cancellationToken = default)
        {
            return DshUsageReader.ReadDetailRows(startLocal, endLocal, includeLiveToday, cancellationToken);
        }

        public IReadOnlyList<TokenUsageBucket> ReadTransientDetailRows(
            DateTimeOffset startLocal,
            DateTimeOffset endLocal,
            CancellationToken cancellationToken = default)
        {
            return DshUsageReader.ReadTransientDetailRows(startLocal, endLocal, cancellationToken);
        }

        public DailyUsageSnapshot ReadDay(
            DateTimeOffset startLocal,
            DateTimeOffset endLocal,
            bool includeLiveToday,
            CancellationToken cancellationToken = default)
        {
            var rows = includeLiveToday
                ? DshUsageReader.ReadDetailRows(startLocal, endLocal, includeLiveToday, cancellationToken)
                : DshUsageReader.ReadCachedDetailRows(startLocal, endLocal, cancellationToken);
            var summary = includeLiveToday
                ? CreateSummaryFromRows(startLocal, endLocal, rows)
                : DshUsageReader.ReadCachedRange(startLocal, endLocal, cancellationToken);
            return new DailyUsageSnapshot(summary, rows);
        }

        public void WarmHistoricalDay(DateTimeOffset dayStart, CancellationToken cancellationToken = default)
        {
            WarmHistoricalDays(new[] { dayStart }, cancellationToken);
        }

        public void WarmHistoricalDays(
            IEnumerable<DateTimeOffset> daysLocal,
            CancellationToken cancellationToken = default,
            Action<DateTimeOffset>? dayCompleted = null,
            Action<int, int>? fileProgress = null)
        {
            DshUsageReader.WarmHistoricalDays(daysLocal, cancellationToken, dayCompleted, fileProgress);
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
        return UsageSummaryBuilder.FromRows(startLocal, endLocal, rows);
    }
}
