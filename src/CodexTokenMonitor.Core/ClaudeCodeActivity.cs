namespace CodexTokenMonitor;

/// <summary>
/// One Claude Code usage block. Claude subscriptions meter usage in 5-hour
/// windows that open with the first request after the previous window closed;
/// the start is floored to the hour, matching how the window is reported.
/// </summary>
internal sealed record ClaudeCodeBlock(
    DateTimeOffset StartLocal,
    DateTimeOffset FirstActivityLocal,
    DateTimeOffset LastActivityLocal,
    TokenUsageBucket Usage)
{
    public DateTimeOffset EndLocal => StartLocal + ClaudeCodeBlockCalculator.BlockLength;

    public bool IsActiveAt(DateTimeOffset nowLocal) =>
        nowLocal < EndLocal && nowLocal - LastActivityLocal < ClaudeCodeBlockCalculator.BlockLength;

    /// <summary>Tokens per minute across the block's active span; null until it spans a minute.</summary>
    public double? TokensPerMinute
    {
        get
        {
            var minutes = (LastActivityLocal - FirstActivityLocal).TotalMinutes;
            return minutes >= 1 ? Usage.TotalTokens / minutes : null;
        }
    }

    /// <summary>Total the block reaches at its end if the current burn rate holds.</summary>
    public long? ProjectedTotalTokens(DateTimeOffset nowLocal)
    {
        if (TokensPerMinute is not { } rate || !IsActiveAt(nowLocal))
        {
            return null;
        }

        var remaining = Math.Max(0, (EndLocal - nowLocal).TotalMinutes);
        return TokenCountMath.AddNonNegative(Usage.TotalTokens, (long)Math.Min(long.MaxValue / 2d, rate * remaining));
    }
}

internal static class ClaudeCodeBlockCalculator
{
    public static readonly TimeSpan BlockLength = TimeSpan.FromHours(5);

    /// <summary>
    /// Groups per-event rows (each row's StartLocal is its timestamp) into 5h
    /// blocks. A new block opens when an event falls past the current block's
    /// end or after a gap of a full block length.
    /// </summary>
    public static IReadOnlyList<ClaudeCodeBlock> Build(IEnumerable<TokenUsageBucket> eventRows)
    {
        var blocks = new List<ClaudeCodeBlock>();
        DateTimeOffset start = default, first = default, last = default;
        TokenUsageBucket? usage = null;
        foreach (var row in eventRows.Where(row => row.Events > 0).OrderBy(row => row.StartLocal))
        {
            var timestamp = row.StartLocal;
            if (usage is null || timestamp >= start + BlockLength || timestamp - last >= BlockLength)
            {
                if (usage is not null)
                {
                    blocks.Add(new ClaudeCodeBlock(start, first, last, usage));
                }

                start = new DateTimeOffset(timestamp.Year, timestamp.Month, timestamp.Day, timestamp.Hour, 0, 0, timestamp.Offset);
                first = timestamp;
                usage = new TokenUsageBucket { StartLocal = start };
            }

            usage.MergeFrom(row);
            last = timestamp;
        }

        if (usage is not null)
        {
            blocks.Add(new ClaudeCodeBlock(start, first, last, usage));
        }

        return blocks;
    }
}

/// <summary>
/// Live account-level view for the Claude Code page header: the current 5h
/// block, today's model mix and a rolling 7-day total. It is independent of
/// the range the page is showing, like the Codex quota and DSH balance panels.
/// </summary>
internal sealed record ClaudeCodeActivitySnapshot(
    DateTimeOffset NowLocal,
    IReadOnlyList<ClaudeCodeBlock> RecentBlocks,
    TokenUsageBucket Today,
    TokenUsageBucket Week,
    int WeekActiveDays)
{
    public ClaudeCodeBlock? CurrentBlock => RecentBlocks.LastOrDefault(block => block.IsActiveAt(NowLocal));
    public ClaudeCodeBlock? LastBlock => RecentBlocks.LastOrDefault();
    public int TodayBlocks => RecentBlocks.Count(block => block.LastActivityLocal >= StartOfDay(NowLocal));

    internal static DateTimeOffset StartOfDay(DateTimeOffset value) =>
        new(value.Year, value.Month, value.Day, 0, 0, 0, value.Offset);
}

internal static class ClaudeCodeActivityReader
{
    // Long enough that a chain of back-to-back blocks reaching into the current
    // one is rebuilt from its real first request, and always covers today.
    private static readonly TimeSpan Lookback = TimeSpan.FromHours(24);
    public const int WeekDays = 7;

    public static ClaudeCodeActivitySnapshot ReadCurrent(DateTimeOffset nowLocal, CancellationToken cancellationToken = default)
    {
        nowLocal = nowLocal.ToOffset(CodexUsageReader.BeijingOffset);
        var todayStart = ClaudeCodeActivitySnapshot.StartOfDay(nowLocal);
        // Transient rows never touch the usage cache, so this live read cannot
        // race the background warmer or the page's own day query.
        var rows = ClaudeUsageReader.ReadTransientDetailRows(nowLocal - Lookback, nowLocal.AddMinutes(1), cancellationToken);
        var today = new TokenUsageBucket { StartLocal = todayStart };
        foreach (var row in rows.Where(row => row.StartLocal >= todayStart))
        {
            today.MergeFrom(row);
        }

        // Earlier days come from the completed day cache only; days still being
        // backfilled are simply absent until the warmer finishes them.
        var history = ClaudeUsageReader.ReadCachedRange(todayStart.AddDays(-(WeekDays - 1)), todayStart, cancellationToken);
        var week = new TokenUsageBucket { StartLocal = todayStart.AddDays(-(WeekDays - 1)) };
        week.MergeFrom(history);
        week.MergeFrom(today);
        var activeDays = history.DailyBuckets.Count(bucket => bucket.Events > 0) + (today.Events > 0 ? 1 : 0);

        return new ClaudeCodeActivitySnapshot(nowLocal, ClaudeCodeBlockCalculator.Build(rows), today, week, activeDays);
    }
}
