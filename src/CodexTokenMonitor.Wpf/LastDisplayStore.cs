using System.Text.Json;

namespace CodexTokenMonitor;

internal static class LastDisplayStore
{
    private const string FolderName = "CodexTokenMonitor";
    private const string FileName = "wpf-last-display.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static LastDisplaySnapshot? Load()
    {
        try
        {
            var path = GetPath();
            if (!File.Exists(path))
            {
                return null;
            }

            var state = JsonSerializer.Deserialize<LastDisplayState>(File.ReadAllText(path), JsonOptions);
            return state is null ? null : ToSnapshot(state);
        }
        catch
        {
            return null;
        }
    }

    public static void Save(UsageSource source, SelectedRange range, UsageQueryResult result)
    {
        try
        {
            var path = GetPath();
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var state = FromSnapshot(new LastDisplaySnapshot(source, range, result));
            File.WriteAllText(path, JsonSerializer.Serialize(state, JsonOptions));
        }
        catch
        {
            // Last display restore is a startup convenience; it should never block usage refresh.
        }
    }

    private static string GetPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, FolderName, FileName);
    }

    private static LastDisplaySnapshot? ToSnapshot(LastDisplayState state)
    {
        if (state.Range is null || state.Result is null || state.Result.Summary is null)
        {
            return null;
        }

        var range = new SelectedRange(
            state.Range.Start,
            state.Range.End,
            state.Range.Title ?? "",
            state.Range.BreakdownTitle ?? "",
            state.Range.Mode,
            state.Range.IsCustomStart);
        var result = new UsageQueryResult(
            ToSummary(state.Result.Summary),
            state.Result.BreakdownRows.Select(ToBucket).ToList(),
            TimeSpan.FromTicks(state.Result.CodingTimeTicks),
            ToQuota(state.Result.Quota),
            state.Result.QuotaSnapshots.Select(ToQuotaSnapshot).ToList());
        return new LastDisplaySnapshot(state.Source, range, result);
    }

    private static LastDisplayState FromSnapshot(LastDisplaySnapshot snapshot)
    {
        return new LastDisplayState
        {
            Source = snapshot.Source,
            SavedAtLocal = DateTimeOffset.UtcNow.ToOffset(CodexUsageReader.BeijingOffset),
            Range = new LastDisplayRangeState
            {
                Start = snapshot.Range.Start,
                End = snapshot.Range.End,
                Title = snapshot.Range.Title,
                BreakdownTitle = snapshot.Range.BreakdownTitle,
                Mode = snapshot.Range.Mode,
                IsCustomStart = snapshot.Range.IsCustomStart
            },
            Result = new LastDisplayResultState
            {
                Summary = FromSummary(snapshot.Result.Summary),
                BreakdownRows = snapshot.Result.BreakdownRows.Select(FromBucket).ToList(),
                CodingTimeTicks = snapshot.Result.CodingTime.Ticks,
                Quota = FromQuota(snapshot.Result.Quota),
                QuotaSnapshots = snapshot.Result.QuotaSnapshots.Select(FromQuotaSnapshot).ToList()
            }
        };
    }

    private static TokenUsageSummary ToSummary(LastDisplaySummaryState state)
    {
        var summary = new TokenUsageSummary
        {
            StartLocal = state.StartLocal,
            EndLocal = state.EndLocal,
            Events = state.Events,
            InputTokens = state.InputTokens,
            CachedInputTokens = state.CachedInputTokens,
            UncachedInputTokens = state.UncachedInputTokens,
            OutputTokens = state.OutputTokens,
            ReasoningOutputTokens = state.ReasoningOutputTokens,
            TotalTokens = state.TotalTokens,
            LastTokenEventLocal = state.LastTokenEventLocal
        };
        summary.DailyBuckets.AddRange(state.DailyBuckets.Select(ToBucket));
        return summary;
    }

    private static LastDisplaySummaryState FromSummary(TokenUsageSummary summary)
    {
        return new LastDisplaySummaryState
        {
            StartLocal = summary.StartLocal,
            EndLocal = summary.EndLocal,
            Events = summary.Events,
            InputTokens = summary.InputTokens,
            CachedInputTokens = summary.CachedInputTokens,
            UncachedInputTokens = summary.UncachedInputTokens,
            OutputTokens = summary.OutputTokens,
            ReasoningOutputTokens = summary.ReasoningOutputTokens,
            TotalTokens = summary.TotalTokens,
            LastTokenEventLocal = summary.LastTokenEventLocal,
            DailyBuckets = summary.DailyBuckets.Select(FromBucket).ToList()
        };
    }

    private static TokenUsageBucket ToBucket(LastDisplayBucketState state)
    {
        return new TokenUsageBucket
        {
            StartLocal = state.StartLocal,
            Events = state.Events,
            InputTokens = state.InputTokens,
            CachedInputTokens = state.CachedInputTokens,
            UncachedInputTokens = state.UncachedInputTokens,
            OutputTokens = state.OutputTokens,
            ReasoningOutputTokens = state.ReasoningOutputTokens,
            TotalTokens = state.TotalTokens,
            LastTokenEventLocal = state.LastTokenEventLocal
        };
    }

    private static LastDisplayBucketState FromBucket(TokenUsageBucket bucket)
    {
        return new LastDisplayBucketState
        {
            StartLocal = bucket.StartLocal,
            Events = bucket.Events,
            InputTokens = bucket.InputTokens,
            CachedInputTokens = bucket.CachedInputTokens,
            UncachedInputTokens = bucket.UncachedInputTokens,
            OutputTokens = bucket.OutputTokens,
            ReasoningOutputTokens = bucket.ReasoningOutputTokens,
            TotalTokens = bucket.TotalTokens,
            LastTokenEventLocal = bucket.LastTokenEventLocal
        };
    }

    private static CodexQuotaEstimate? ToQuota(LastDisplayQuotaState? state)
    {
        return state is null
            ? null
            : new CodexQuotaEstimate(
                state.SnapshotLocal,
                state.LimitId,
                state.LimitName,
                ToQuotaWindow(state.FiveHour),
                ToQuotaWindow(state.Week));
    }

    private static LastDisplayQuotaState? FromQuota(CodexQuotaEstimate? quota)
    {
        return quota is null
            ? null
            : new LastDisplayQuotaState
            {
                SnapshotLocal = quota.SnapshotLocal,
                LimitId = quota.LimitId,
                LimitName = quota.LimitName,
                FiveHour = FromQuotaWindow(quota.FiveHour),
                Week = FromQuotaWindow(quota.Week)
            };
    }

    private static CodexQuotaWindowEstimate? ToQuotaWindow(LastDisplayQuotaWindowState? state)
    {
        return state is null
            ? null
            : new CodexQuotaWindowEstimate(
                state.Label ?? "",
                state.UsedPercent,
                state.WindowMinutes,
                state.WindowStartLocal,
                state.WindowEndLocal,
                state.ResetAtLocal,
                ToSummary(state.Usage),
                state.UsedGptCost,
                state.EstimatedGptLimit,
                state.EstimatedTokenLimit);
    }

    private static LastDisplayQuotaWindowState? FromQuotaWindow(CodexQuotaWindowEstimate? window)
    {
        return window is null
            ? null
            : new LastDisplayQuotaWindowState
            {
                Label = window.Label,
                UsedPercent = window.UsedPercent,
                WindowMinutes = window.WindowMinutes,
                WindowStartLocal = window.WindowStartLocal,
                WindowEndLocal = window.WindowEndLocal,
                ResetAtLocal = window.ResetAtLocal,
                Usage = FromSummary(window.Usage),
                UsedGptCost = window.UsedGptCost,
                EstimatedGptLimit = window.EstimatedGptLimit,
                EstimatedTokenLimit = window.EstimatedTokenLimit
            };
    }

    private static CodexQuotaSnapshot ToQuotaSnapshot(LastDisplayQuotaSnapshotState state)
    {
        return new CodexQuotaSnapshot(
            state.SnapshotLocal,
            state.LimitId,
            state.LimitName,
            state.FiveHourUsedPercent,
            state.FiveHourResetAtLocal,
            state.WeekUsedPercent,
            state.WeekResetAtLocal);
    }

    private static LastDisplayQuotaSnapshotState FromQuotaSnapshot(CodexQuotaSnapshot snapshot)
    {
        return new LastDisplayQuotaSnapshotState
        {
            SnapshotLocal = snapshot.SnapshotLocal,
            LimitId = snapshot.LimitId,
            LimitName = snapshot.LimitName,
            FiveHourUsedPercent = snapshot.FiveHourUsedPercent,
            FiveHourResetAtLocal = snapshot.FiveHourResetAtLocal,
            WeekUsedPercent = snapshot.WeekUsedPercent,
            WeekResetAtLocal = snapshot.WeekResetAtLocal
        };
    }
}

internal sealed record LastDisplaySnapshot(UsageSource Source, SelectedRange Range, UsageQueryResult Result);

internal sealed class LastDisplayState
{
    public UsageSource Source { get; set; }
    public DateTimeOffset SavedAtLocal { get; set; }
    public LastDisplayRangeState? Range { get; set; }
    public LastDisplayResultState? Result { get; set; }
}

internal sealed class LastDisplayRangeState
{
    public DateTimeOffset Start { get; set; }
    public DateTimeOffset End { get; set; }
    public string? Title { get; set; }
    public string? BreakdownTitle { get; set; }
    public RangeMode Mode { get; set; }
    public bool IsCustomStart { get; set; }
}

internal sealed class LastDisplayResultState
{
    public LastDisplaySummaryState? Summary { get; set; }
    public List<LastDisplayBucketState> BreakdownRows { get; set; } = new();
    public long CodingTimeTicks { get; set; }
    public LastDisplayQuotaState? Quota { get; set; }
    public List<LastDisplayQuotaSnapshotState> QuotaSnapshots { get; set; } = new();
}

internal class LastDisplayBucketState
{
    public DateTimeOffset StartLocal { get; set; }
    public long Events { get; set; }
    public long InputTokens { get; set; }
    public long CachedInputTokens { get; set; }
    public long UncachedInputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long ReasoningOutputTokens { get; set; }
    public long TotalTokens { get; set; }
    public DateTimeOffset? LastTokenEventLocal { get; set; }
}

internal sealed class LastDisplaySummaryState : LastDisplayBucketState
{
    public DateTimeOffset EndLocal { get; set; }
    public List<LastDisplayBucketState> DailyBuckets { get; set; } = new();
}

internal sealed class LastDisplayQuotaState
{
    public DateTimeOffset SnapshotLocal { get; set; }
    public string? LimitId { get; set; }
    public string? LimitName { get; set; }
    public LastDisplayQuotaWindowState? FiveHour { get; set; }
    public LastDisplayQuotaWindowState? Week { get; set; }
}

internal sealed class LastDisplayQuotaWindowState
{
    public string? Label { get; set; }
    public decimal UsedPercent { get; set; }
    public int WindowMinutes { get; set; }
    public DateTimeOffset WindowStartLocal { get; set; }
    public DateTimeOffset WindowEndLocal { get; set; }
    public DateTimeOffset? ResetAtLocal { get; set; }
    public LastDisplaySummaryState Usage { get; set; } = new();
    public decimal UsedGptCost { get; set; }
    public decimal? EstimatedGptLimit { get; set; }
    public long? EstimatedTokenLimit { get; set; }
}

internal sealed class LastDisplayQuotaSnapshotState
{
    public DateTimeOffset SnapshotLocal { get; set; }
    public string? LimitId { get; set; }
    public string? LimitName { get; set; }
    public decimal? FiveHourUsedPercent { get; set; }
    public DateTimeOffset? FiveHourResetAtLocal { get; set; }
    public decimal? WeekUsedPercent { get; set; }
    public DateTimeOffset? WeekResetAtLocal { get; set; }
}
