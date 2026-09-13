using System.Text.Json;

namespace CodexTokenMonitor;

internal static class LastDisplayStore
{
    private const string FolderName = "CodexTokenMonitor";
    private const string FileName = "wpf-last-display-v5.json";
    private static readonly JsonSerializerOptions JsonOptions = new();
    private static readonly object SyncRoot = new();
    private static readonly SemaphoreSlim WriteGate = new(1, 1);
    private static readonly AsyncLocal<WriteTestHooks?> TestHooks = new();
    private static LastDisplaySnapshot? pendingSnapshot;
    private static CancellationTokenSource? pendingSave;
    private static long saveVersion;

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
        // DetailRows are only needed by the live view. Keeping them in the
        // debounced restore snapshot makes every refresh retain the complete
        // detail list until the background write completes.
        var compactResult = result with { DetailRows = Array.Empty<TokenUsageBucket>() };
        var snapshot = new LastDisplaySnapshot(source, range, compactResult);
        CancellationTokenSource cts;
        long version;
        lock (SyncRoot)
        {
            pendingSnapshot = snapshot;
            pendingSave?.Cancel();
            pendingSave?.Dispose();
            pendingSave = cts = new CancellationTokenSource();
            version = ++saveVersion;
        }

        _ = SaveLaterAsync(snapshot, version, cts.Token);
    }

    public static void Flush()
    {
        // Compatibility for non-async callers. Both the writer and this flush
        // release their gate independently of any UI SynchronizationContext.
        FlushAsync().GetAwaiter().GetResult();
    }

    public static async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        lock (SyncRoot)
        {
            pendingSave?.Cancel();
            if (pendingSnapshot is null)
            {
                return;
            }
        }

        await WriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LastDisplaySnapshot? snapshot;
            lock (SyncRoot)
            {
                // A newer Save can arrive while the previous writer owns the
                // gate. Flush the newest snapshot and cancel its debounce too.
                pendingSave?.Cancel();
                snapshot = pendingSnapshot;
            }

            if (snapshot is not null)
            {
                await Task.Run(() => WriteSnapshot(snapshot), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            WriteGate.Release();
        }
    }

    private static async Task SaveLaterAsync(LastDisplaySnapshot snapshot, long version, CancellationToken token)
    {
        try
        {
            await (TestHooks.Value?.DebounceDelay(token) ?? Task.Delay(500, token)).ConfigureAwait(false);
            await WriteGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (version != Volatile.Read(ref saveVersion))
                {
                    return;
                }

                await Task.Run(() => WriteSnapshot(snapshot), token).ConfigureAwait(false);
            }
            finally
            {
                WriteGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // Last display restore is a startup convenience; it should never block usage refresh.
        }
    }

    private static void WriteSnapshot(LastDisplaySnapshot snapshot)
    {
        TestHooks.Value?.BeforeWrite();
        WriteState(FromSnapshot(snapshot));
    }

    // Test scheduling is local to the caller's execution context. The hooks
    // allow a writer to pause while owning its gate without timing-based sleeps.
    internal static IDisposable PushWriteTestHooks(Func<CancellationToken, Task> debounceDelay, Action beforeWrite)
    {
        var previous = TestHooks.Value;
        TestHooks.Value = new WriteTestHooks(debounceDelay, beforeWrite);
        return new WriteTestHookScope(previous);
    }

    private sealed record WriteTestHooks(Func<CancellationToken, Task> DebounceDelay, Action BeforeWrite);

    private sealed class WriteTestHookScope(WriteTestHooks? previous) : IDisposable
    {
        public void Dispose() => TestHooks.Value = previous;
    }

    private static void WriteState(LastDisplayState state)
    {
        try
        {
            var path = GetPath();
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(state, JsonOptions));
                File.Move(temporaryPath, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
        catch
        {
            // Last display restore is a startup convenience; it should never block usage refresh.
        }
    }

    private static string GetPath()
    {
        return Path.Combine(MonitorCachePaths.LocalAppData, FolderName, FileName);
    }

    private static LastDisplaySnapshot? ToSnapshot(LastDisplayState state)
    {
        if (state.Range is null || state.Result is null || state.Result.Summary is null)
        {
            return null;
        }

        var range = new SelectedRange(
            Normalize(state.Range.Start),
            Normalize(state.Range.End),
            state.Range.Title ?? "",
            state.Range.BreakdownTitle ?? "",
            state.Range.Mode,
            state.Range.IsCustomStart,
            state.Range.FollowsCurrent);
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
                Start = Normalize(snapshot.Range.Start),
                End = Normalize(snapshot.Range.End),
                Title = snapshot.Range.Title,
                BreakdownTitle = snapshot.Range.BreakdownTitle,
                Mode = snapshot.Range.Mode,
                IsCustomStart = snapshot.Range.IsCustomStart,
                FollowsCurrent = snapshot.Range.FollowsCurrent
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
            StartLocal = Normalize(state.StartLocal),
            EndLocal = Normalize(state.EndLocal),
            Events = state.Events,
            InputTokens = state.InputTokens,
            CachedInputTokens = state.CachedInputTokens,
            CacheWriteInputTokens = state.CacheWriteInputTokens,
            ModelUsage = state.ModelUsage,
            UncachedInputTokens = state.UncachedInputTokens,
            OutputTokens = state.OutputTokens,
            ReasoningOutputTokens = state.ReasoningOutputTokens,
            TotalTokens = state.TotalTokens,
            LongContextEvents = state.LongContextEvents,
            LongContextInputTokens = state.LongContextInputTokens,
            LongContextCachedInputTokens = state.LongContextCachedInputTokens,
            LongContextCacheWriteInputTokens = state.LongContextCacheWriteInputTokens,
            LongContextOutputTokens = state.LongContextOutputTokens,
            PeakInputTokens = state.PeakInputTokens,
            PeakCachedInputTokens = state.PeakCachedInputTokens,
            PeakCacheWriteInputTokens = state.PeakCacheWriteInputTokens,
            PeakOutputTokens = state.PeakOutputTokens,
            LastTokenEventLocal = Normalize(state.LastTokenEventLocal)
        };
        summary.DailyBuckets.AddRange(state.DailyBuckets.Select(ToBucket));
        return summary;
    }

    private static LastDisplaySummaryState FromSummary(TokenUsageSummary summary)
    {
        return new LastDisplaySummaryState
        {
            StartLocal = Normalize(summary.StartLocal),
            EndLocal = Normalize(summary.EndLocal),
            Events = summary.Events,
            InputTokens = summary.InputTokens,
            CachedInputTokens = summary.CachedInputTokens,
            CacheWriteInputTokens = summary.CacheWriteInputTokens,
            ModelUsage = summary.ModelUsage,
            UncachedInputTokens = summary.UncachedInputTokens,
            OutputTokens = summary.OutputTokens,
            ReasoningOutputTokens = summary.ReasoningOutputTokens,
            TotalTokens = summary.TotalTokens,
            LongContextEvents = summary.LongContextEvents,
            LongContextInputTokens = summary.LongContextInputTokens,
            LongContextCachedInputTokens = summary.LongContextCachedInputTokens,
            LongContextCacheWriteInputTokens = summary.LongContextCacheWriteInputTokens,
            LongContextOutputTokens = summary.LongContextOutputTokens,
            PeakInputTokens = summary.PeakInputTokens,
            PeakCachedInputTokens = summary.PeakCachedInputTokens,
            PeakCacheWriteInputTokens = summary.PeakCacheWriteInputTokens,
            PeakOutputTokens = summary.PeakOutputTokens,
            LastTokenEventLocal = Normalize(summary.LastTokenEventLocal),
            DailyBuckets = summary.DailyBuckets.Select(FromBucket).ToList()
        };
    }

    private static TokenUsageBucket ToBucket(LastDisplayBucketState state)
    {
        return new TokenUsageBucket
        {
            StartLocal = Normalize(state.StartLocal),
            Events = state.Events,
            InputTokens = state.InputTokens,
            CachedInputTokens = state.CachedInputTokens,
            CacheWriteInputTokens = state.CacheWriteInputTokens,
            ModelUsage = state.ModelUsage,
            UncachedInputTokens = state.UncachedInputTokens,
            OutputTokens = state.OutputTokens,
            ReasoningOutputTokens = state.ReasoningOutputTokens,
            TotalTokens = state.TotalTokens,
            LongContextEvents = state.LongContextEvents,
            LongContextInputTokens = state.LongContextInputTokens,
            LongContextCachedInputTokens = state.LongContextCachedInputTokens,
            LongContextCacheWriteInputTokens = state.LongContextCacheWriteInputTokens,
            LongContextOutputTokens = state.LongContextOutputTokens,
            PeakInputTokens = state.PeakInputTokens,
            PeakCachedInputTokens = state.PeakCachedInputTokens,
            PeakCacheWriteInputTokens = state.PeakCacheWriteInputTokens,
            PeakOutputTokens = state.PeakOutputTokens,
            LastTokenEventLocal = Normalize(state.LastTokenEventLocal)
        };
    }

    private static LastDisplayBucketState FromBucket(TokenUsageBucket bucket)
    {
        return new LastDisplayBucketState
        {
            StartLocal = Normalize(bucket.StartLocal),
            Events = bucket.Events,
            InputTokens = bucket.InputTokens,
            CachedInputTokens = bucket.CachedInputTokens,
            CacheWriteInputTokens = bucket.CacheWriteInputTokens,
            ModelUsage = bucket.ModelUsage,
            UncachedInputTokens = bucket.UncachedInputTokens,
            OutputTokens = bucket.OutputTokens,
            ReasoningOutputTokens = bucket.ReasoningOutputTokens,
            TotalTokens = bucket.TotalTokens,
            LongContextEvents = bucket.LongContextEvents,
            LongContextInputTokens = bucket.LongContextInputTokens,
            LongContextCachedInputTokens = bucket.LongContextCachedInputTokens,
            LongContextCacheWriteInputTokens = bucket.LongContextCacheWriteInputTokens,
            LongContextOutputTokens = bucket.LongContextOutputTokens,
            PeakInputTokens = bucket.PeakInputTokens,
            PeakCachedInputTokens = bucket.PeakCachedInputTokens,
            PeakCacheWriteInputTokens = bucket.PeakCacheWriteInputTokens,
            PeakOutputTokens = bucket.PeakOutputTokens,
            LastTokenEventLocal = Normalize(bucket.LastTokenEventLocal)
        };
    }

    private static CodexQuotaEstimate? ToQuota(LastDisplayQuotaState? state)
    {
        return state is null
            ? null
            : new CodexQuotaEstimate(
                Normalize(state.SnapshotLocal),
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
                SnapshotLocal = Normalize(quota.SnapshotLocal),
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
                Normalize(state.WindowStartLocal),
                Normalize(state.WindowEndLocal),
                Normalize(state.ResetAtLocal),
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
                WindowStartLocal = Normalize(window.WindowStartLocal),
                WindowEndLocal = Normalize(window.WindowEndLocal),
                ResetAtLocal = Normalize(window.ResetAtLocal),
                Usage = FromSummary(window.Usage),
                UsedGptCost = window.UsedGptCost,
                EstimatedGptLimit = window.EstimatedGptLimit,
                EstimatedTokenLimit = window.EstimatedTokenLimit
            };
    }

    private static CodexQuotaSnapshot ToQuotaSnapshot(LastDisplayQuotaSnapshotState state)
    {
        return new CodexQuotaSnapshot(
            Normalize(state.SnapshotLocal),
            state.LimitId,
            state.LimitName,
            state.FiveHourUsedPercent,
            Normalize(state.FiveHourResetAtLocal),
            state.WeekUsedPercent,
            Normalize(state.WeekResetAtLocal));
    }

    private static LastDisplayQuotaSnapshotState FromQuotaSnapshot(CodexQuotaSnapshot snapshot)
    {
        return new LastDisplayQuotaSnapshotState
        {
            SnapshotLocal = Normalize(snapshot.SnapshotLocal),
            LimitId = snapshot.LimitId,
            LimitName = snapshot.LimitName,
            FiveHourUsedPercent = snapshot.FiveHourUsedPercent,
            FiveHourResetAtLocal = Normalize(snapshot.FiveHourResetAtLocal),
            WeekUsedPercent = snapshot.WeekUsedPercent,
            WeekResetAtLocal = Normalize(snapshot.WeekResetAtLocal)
        };
    }

    private static DateTimeOffset Normalize(DateTimeOffset value)
    {
        return value.ToOffset(CodexUsageReader.BeijingOffset);
    }

    private static DateTimeOffset? Normalize(DateTimeOffset? value)
    {
        return value?.ToOffset(CodexUsageReader.BeijingOffset);
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
    public bool FollowsCurrent { get; set; }
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
    public Dictionary<string, TokenUsageBucket> ModelUsage { get; set; } = new();
    public DateTimeOffset StartLocal { get; set; }
    public long Events { get; set; }
    public long InputTokens { get; set; }
    public long CachedInputTokens { get; set; }
    public long CacheWriteInputTokens { get; set; }
    public long UncachedInputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long ReasoningOutputTokens { get; set; }
    public long TotalTokens { get; set; }
    public long LongContextEvents { get; set; }
    public long LongContextInputTokens { get; set; }
    public long LongContextCachedInputTokens { get; set; }
    public long LongContextCacheWriteInputTokens { get; set; }
    public long LongContextOutputTokens { get; set; }
    public long PeakInputTokens { get; set; }
    public long PeakCachedInputTokens { get; set; }
    public long PeakCacheWriteInputTokens { get; set; }
    public long PeakOutputTokens { get; set; }
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
