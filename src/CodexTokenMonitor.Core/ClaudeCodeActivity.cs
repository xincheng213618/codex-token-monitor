using System.Globalization;
using System.Text.Json;

namespace CodexTokenMonitor;

/// <summary>
/// One Claude Code usage block. Claude subscriptions meter usage in 5-hour
/// windows that open with the first request after the previous window closed
/// (/usage shows resets such as 5:50, so the start is not rounded to the hour).
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
    /// blocks. A new block opens with the first event past the current block's
    /// end. A known block start (<paramref name="anchorLocal"/>) pins the chain:
    /// earlier rows are ignored and the first block starts there, so a moving
    /// read window cannot shift block boundaries during continuous use.
    /// </summary>
    public static IReadOnlyList<ClaudeCodeBlock> Build(IEnumerable<TokenUsageBucket> eventRows, DateTimeOffset? anchorLocal = null)
    {
        var blocks = new List<ClaudeCodeBlock>();
        DateTimeOffset start = default, first = default, last = default;
        TokenUsageBucket? usage = null;
        var ordered = eventRows
            .Where(row => row.Events > 0 && (anchorLocal is null || row.StartLocal >= anchorLocal.Value))
            .OrderBy(row => row.StartLocal);
        foreach (var row in ordered)
        {
            var timestamp = row.StartLocal;
            if (usage is null || timestamp >= start + BlockLength)
            {
                if (usage is not null)
                {
                    blocks.Add(new ClaudeCodeBlock(start, first, last, usage));
                }

                start = usage is null && anchorLocal is { } anchor && timestamp < anchor + BlockLength ? anchor : timestamp;
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

/// <summary>One official rate-limit window as Claude Code reports it to the status line.</summary>
internal sealed record ClaudeRateLimitWindow(double UsedPercent, DateTimeOffset ResetLocal);

internal sealed record ClaudeRateLimitSnapshot(
    DateTimeOffset RecordedLocal,
    ClaudeRateLimitWindow? FiveHour,
    ClaudeRateLimitWindow? SevenDay);

/// <summary>
/// Reads the rate-limit log written by Scripts/claude-statusline.ps1. Claude
/// Code hands the status line official <c>rate_limits</c> (used percentage and
/// <c>resets_at</c>) for claude.ai Pro/Max sessions; nothing else records them.
/// </summary>
internal static class ClaudeRateLimitLog
{
    public static string FilePath =>
        Path.Combine(MonitorCachePaths.LocalAppData, "ClaudeCodeTokenMonitor", "rate-limits.jsonl");

    /// <summary>The newest still-open 5h and 7d windows; a window is dropped once its reset passes.</summary>
    public static ClaudeRateLimitSnapshot? ReadLatest(DateTimeOffset nowLocal)
    {
        string[] lines;
        try
        {
            if (!File.Exists(FilePath))
            {
                return null;
            }

            using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            lines = reader.ReadToEnd().Split('\n');
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return Latest(Parse(lines), nowLocal);
    }

    public static ClaudeRateLimitSnapshot? Latest(IEnumerable<ClaudeRateLimitSnapshot> snapshots, DateTimeOffset nowLocal)
    {
        var list = snapshots.OrderBy(item => item.RecordedLocal).ToList();
        var fiveHour = list.LastOrDefault(item => item.FiveHour is { } window && window.ResetLocal > nowLocal);
        var sevenDay = list.LastOrDefault(item => item.SevenDay is { } window && window.ResetLocal > nowLocal);
        if (fiveHour is null && sevenDay is null)
        {
            return null;
        }

        return new ClaudeRateLimitSnapshot(
            new[] { fiveHour?.RecordedLocal, sevenDay?.RecordedLocal }.Max()!.Value,
            fiveHour?.FiveHour,
            sevenDay?.SevenDay);
    }

    public static IReadOnlyList<ClaudeRateLimitSnapshot> Parse(IEnumerable<string> lines)
    {
        var result = new List<ClaudeRateLimitSnapshot>();
        foreach (var line in lines)
        {
            var text = line.Trim().TrimStart('﻿');
            if (text.Length == 0)
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(text);
                var root = document.RootElement;
                if (!root.TryGetProperty("recorded_at", out var recorded) || !recorded.TryGetInt64(out var recordedSeconds))
                {
                    continue;
                }

                var snapshot = new ClaudeRateLimitSnapshot(
                    ToLocal(recordedSeconds),
                    ReadWindow(root, "five_hour"),
                    ReadWindow(root, "seven_day"));
                if (snapshot.FiveHour is not null || snapshot.SevenDay is not null)
                {
                    result.Add(snapshot);
                }
            }
            catch (JsonException)
            {
                // A torn last line from a concurrent append is skipped.
            }
        }

        return result;
    }

    private static ClaudeRateLimitWindow? ReadWindow(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object ||
            !window.TryGetProperty("used_percentage", out var used) || used.ValueKind != JsonValueKind.Number ||
            !window.TryGetProperty("resets_at", out var reset) || !reset.TryGetInt64(out var resetSeconds))
        {
            return null;
        }

        return new ClaudeRateLimitWindow(Math.Clamp(used.GetDouble(), 0, 100), ToLocal(resetSeconds));
    }

    private static DateTimeOffset ToLocal(long epochSeconds) =>
        DateTimeOffset.FromUnixTimeSeconds(epochSeconds).ToOffset(CodexUsageReader.BeijingOffset);
}

internal enum ClaudeWindowSource
{
    Official,
    Manual,
    Inferred
}

/// <summary>
/// Usage inside one quota window, measured over the window itself
/// (reset − length → now), never over calendar days or a guessed block.
/// </summary>
internal sealed record ClaudeQuotaWindowUsage(
    ClaudeQuotaWindowKind Kind,
    ClaudeWindowSource Source,
    DateTimeOffset StartLocal,
    DateTimeOffset ResetLocal,
    TokenUsageBucket Usage,
    double? OfficialPercent,
    bool IsComplete)
{
    public TokenUsageBucket ClaudeUsage => ClaudeQuotaEstimator.ClaudeModelUsage(new[] { Usage }, StartLocal, DateTimeOffset.MaxValue);
}

/// <summary>
/// Live account-level view for the Claude Code page header: the current block,
/// the 5h and 7d quota windows, today's model mix and a calendar 7-day total.
/// It is independent of the range the page is showing.
/// </summary>
internal sealed record ClaudeCodeActivitySnapshot(
    DateTimeOffset NowLocal,
    IReadOnlyList<ClaudeCodeBlock> RecentBlocks,
    TokenUsageBucket Today,
    TokenUsageBucket Week,
    int WeekActiveDays,
    bool WeekIsComplete = true,
    ClaudeQuotaWindowUsage? FiveHourWindow = null,
    ClaudeQuotaWindowUsage? WeekWindow = null)
{
    public ClaudeCodeBlock? CurrentBlock => RecentBlocks.LastOrDefault(block => block.IsActiveAt(NowLocal));
    public ClaudeCodeBlock? LastBlock => RecentBlocks.LastOrDefault();

    internal static DateTimeOffset StartOfDay(DateTimeOffset value) =>
        new(value.Year, value.Month, value.Day, 0, 0, 0, value.Offset);
}

internal static class ClaudeCodeActivityReader
{
    private static readonly TimeSpan Lookback = TimeSpan.FromHours(24);
    // A saved block start older than this no longer describes a chain worth
    // reading back to; the next block then starts at its own first request.
    private static readonly TimeSpan AnchorMaxAge = TimeSpan.FromHours(72);
    private static readonly TimeSpan IncompleteRetry = TimeSpan.FromMinutes(2);
    public const int WeekDays = 7;

    private static readonly object HistoryGate = new();
    private static readonly Dictionary<(string Scope, DateTimeOffset Start, DateTimeOffset End), HistoryPart> HistoryMemo = new();

    private static string AnchorPath =>
        Path.Combine(MonitorCachePaths.LocalAppData, "ClaudeCodeTokenMonitor", "block-anchor.txt");

    public static ClaudeCodeActivitySnapshot ReadCurrent(
        DateTimeOffset nowLocal,
        IReadOnlyList<ClaudeQuotaReading>? readings = null,
        CancellationToken cancellationToken = default)
    {
        nowLocal = nowLocal.ToOffset(CodexUsageReader.BeijingOffset);
        var todayStart = ClaudeCodeActivitySnapshot.StartOfDay(nowLocal);
        var official = ClaudeRateLimitLog.ReadLatest(nowLocal);

        // The official reset is the truth for the block chain; otherwise reuse
        // the block start saved by earlier refreshes.
        var anchor = official?.FiveHour is { } officialFive
            ? officialFive.ResetLocal - ClaudeCodeBlockCalculator.BlockLength
            : LoadAnchor(nowLocal);
        var lookbackStart = anchor is { } a && a < nowLocal - Lookback ? a : nowLocal - Lookback;

        // Transient rows never touch the usage cache, so this live read cannot
        // race the background warmer or the page's own day query.
        var (rows, rowsComplete) = ClaudeUsageReader.ReadTransientDetailRowsWithStatus(lookbackStart, nowLocal.AddMinutes(1), cancellationToken);
        var blocks = ClaudeCodeBlockCalculator.Build(rows, anchor);
        if (blocks.Count > 0)
        {
            SaveAnchor(blocks[^1].StartLocal);
        }

        var today = Merge(rows.Where(row => row.StartLocal >= todayStart), todayStart);
        var history = ReadHistory(todayStart.AddDays(-(WeekDays - 1)), todayStart, cancellationToken);
        var week = new TokenUsageBucket { StartLocal = todayStart.AddDays(-(WeekDays - 1)) };
        week.MergeFrom(history.Usage);
        week.MergeFrom(today);
        var snapshot = new ClaudeCodeActivitySnapshot(nowLocal, blocks, today, week,
            history.ActiveDays + (today.Events > 0 ? 1 : 0), history.IsComplete && rowsComplete);

        ClaudeQuotaWindowUsage? Window(ClaudeQuotaWindowKind kind, ClaudeWindowSource source, DateTimeOffset reset, double? percent)
        {
            var start = reset - ClaudeQuotaEstimator.Length(kind);
            var (usage, complete) = UsageSince(start, nowLocal, lookbackStart, rows, rowsComplete, todayStart, cancellationToken);
            return new ClaudeQuotaWindowUsage(kind, source, start, reset, usage, percent, complete);
        }

        ClaudeQuotaWindowUsage? Resolve(ClaudeQuotaWindowKind kind, ClaudeRateLimitWindow? officialWindow)
        {
            if (officialWindow is not null)
            {
                return Window(kind, ClaudeWindowSource.Official, officialWindow.ResetLocal, officialWindow.UsedPercent);
            }

            var manual = readings?
                .Where(item => item.Kind == kind && item.ResetLocal > nowLocal)
                .MaxBy(item => item.RecordedLocal);
            if (manual is not null)
            {
                return Window(kind, ClaudeWindowSource.Manual, manual.ResetLocal, null);
            }

            return kind == ClaudeQuotaWindowKind.FiveHour && snapshot.CurrentBlock is { } block
                ? Window(kind, ClaudeWindowSource.Inferred, block.EndLocal, null)
                : null;
        }

        return snapshot with
        {
            FiveHourWindow = Resolve(ClaudeQuotaWindowKind.FiveHour, official?.FiveHour),
            WeekWindow = Resolve(ClaudeQuotaWindowKind.Week, official?.SevenDay)
        };
    }

    private static (TokenUsageBucket Usage, bool IsComplete) UsageSince(
        DateTimeOffset start,
        DateTimeOffset nowLocal,
        DateTimeOffset lookbackStart,
        IReadOnlyList<TokenUsageBucket> rows,
        bool rowsComplete,
        DateTimeOffset todayStart,
        CancellationToken cancellationToken)
    {
        if (start >= lookbackStart)
        {
            return (Merge(rows.Where(row => row.StartLocal >= start && row.StartLocal <= nowLocal), start), rowsComplete);
        }

        // Older than the live rows: the part before today is stable for the
        // whole day, so it is read once and remembered.
        var history = ReadHistory(start, todayStart, cancellationToken);
        var usage = Merge(rows.Where(row => row.StartLocal >= todayStart && row.StartLocal <= nowLocal), start);
        usage.MergeFrom(history.Usage);
        return (usage, history.IsComplete && rowsComplete);
    }

    /// <summary>
    /// Usage in [start, end) from completed cache days, scanning the logs for
    /// partial days and for days the cache has not finished; a scan that could
    /// not read every file marks the result incomplete instead of dropping it.
    /// </summary>
    private static HistoryPart ReadHistory(DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken)
    {
        if (start >= end)
        {
            return new HistoryPart(new TokenUsageBucket { StartLocal = start }, 0, true, BeijingClock.Now);
        }

        // Isolated cache/log roots (tests, probes) must not share remembered days.
        var key = (MonitorCachePaths.LocalAppData + "|" + UsageLogPaths.GetOverrideRoot(UsageSource.ClaudeCode), start, end);
        lock (HistoryGate)
        {
            if (HistoryMemo.TryGetValue(key, out var cached) &&
                (cached.IsComplete || BeijingClock.Now - cached.ReadAt < IncompleteRetry))
            {
                return cached;
            }
        }

        var firstDay = ClaudeCodeActivitySnapshot.StartOfDay(start);
        var incomplete = ClaudeUsageReader.GetIncompleteHistoricalDays(firstDay, end.AddTicks(-1), cancellationToken).ToHashSet();
        var usage = new TokenUsageBucket { StartLocal = start };
        var activeDays = 0;
        var complete = true;
        for (var day = firstDay; day < end; day = day.AddDays(1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var segmentStart = day < start ? start : day;
            var segmentEnd = day.AddDays(1) > end ? end : day.AddDays(1);
            TokenUsageBucket dayUsage;
            if (segmentStart == day && segmentEnd == day.AddDays(1) && !incomplete.Contains(day))
            {
                dayUsage = ClaudeUsageReader.ReadCachedRange(day, segmentEnd, cancellationToken);
            }
            else
            {
                var (rows, rowsComplete) = ClaudeUsageReader.ReadTransientDetailRowsWithStatus(segmentStart, segmentEnd, cancellationToken);
                complete &= rowsComplete;
                dayUsage = Merge(rows, segmentStart);
            }

            if (dayUsage.Events > 0)
            {
                activeDays++;
                usage.MergeFrom(dayUsage);
            }
        }

        var part = new HistoryPart(usage, activeDays, complete, BeijingClock.Now);
        lock (HistoryGate)
        {
            // Keys roll over at midnight; drop the previous day's entries.
            foreach (var stale in HistoryMemo.Keys.Where(item => item.End != end).ToList())
            {
                HistoryMemo.Remove(stale);
            }

            HistoryMemo[key] = part;
        }

        return part;
    }

    internal static void ResetHistoryMemo()
    {
        lock (HistoryGate)
        {
            HistoryMemo.Clear();
        }
    }

    private static TokenUsageBucket Merge(IEnumerable<TokenUsageBucket> rows, DateTimeOffset start)
    {
        var bucket = new TokenUsageBucket { StartLocal = start };
        foreach (var row in rows)
        {
            bucket.MergeFrom(row);
        }

        return bucket;
    }

    private static DateTimeOffset? LoadAnchor(DateTimeOffset nowLocal)
    {
        try
        {
            if (File.Exists(AnchorPath) &&
                DateTimeOffset.TryParse(File.ReadAllText(AnchorPath).Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var anchor) &&
                anchor <= nowLocal && nowLocal - anchor <= AnchorMaxAge)
            {
                return anchor.ToOffset(CodexUsageReader.BeijingOffset);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    private static void SaveAnchor(DateTimeOffset anchor)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AnchorPath)!);
            File.WriteAllText(AnchorPath, anchor.ToString("O", CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing the anchor only costs block stability after a long gap.
        }
    }

    private sealed record HistoryPart(TokenUsageBucket Usage, int ActiveDays, bool IsComplete, DateTimeOffset ReadAt);
}
