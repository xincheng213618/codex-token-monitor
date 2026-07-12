using System.Windows.Threading;

namespace CodexTokenMonitor;

internal sealed class BackgroundCacheWarmer : IDisposable
{
    private const int BackgroundCacheIntervalMs = 120_000;

    private static readonly DateTimeOffset BackgroundCacheStart = new(
        2026,
        1,
        1,
        0,
        0,
        0,
        CodexUsageReader.BeijingOffset);

    private readonly Func<UsageSource> currentSource;
    private readonly Func<bool> isForegroundBusy;
    private readonly SemaphoreSlim workGate;
    private readonly Action<string> setStatus;
    private readonly DispatcherTimer timer = new();
    private CancellationTokenSource? cts;
    private bool isRunning;
    private bool disposed;

    public BackgroundCacheWarmer(
        Func<UsageSource> currentSource,
        Func<bool> isForegroundBusy,
        SemaphoreSlim workGate,
        Action<string> setStatus)
    {
        this.currentSource = currentSource;
        this.isForegroundBusy = isForegroundBusy;
        this.workGate = workGate;
        this.setStatus = setStatus;
        timer.Interval = TimeSpan.FromMilliseconds(BackgroundCacheIntervalMs);
        timer.Tick += Timer_Tick;
    }

    public bool IsRunning => isRunning;

    public void Start()
    {
        if (disposed)
        {
            return;
        }

        timer.Start();
        _ = WarmNowAsync();
    }

    public void Stop()
    {
        timer.Stop();
        CancelCurrent();
    }

    public void CancelCurrent()
    {
        cts?.Cancel();
    }

    public async Task WarmNowAsync()
    {
        if (isRunning || disposed)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow.ToOffset(CodexUsageReader.BeijingOffset);
        var lastHistoricalDay = StartOfDay(now).AddDays(-1);
        if (lastHistoricalDay < BackgroundCacheStart)
        {
            return;
        }

        cts?.Cancel();
        var localCts = new CancellationTokenSource();
        cts = localCts;
        var token = localCts.Token;
        isRunning = true;
        timer.Stop();

        try
        {
            var sources = GetSourceOrder();
            var pending = sources.ToDictionary(
                source => source,
                source => UsageSourceReaders.For(source).GetIncompleteHistoricalDays(BackgroundCacheStart, lastHistoricalDay)
                    .Select(day => DateOnly.FromDateTime(day.DateTime))
                    .ToHashSet());
            var pendingQuota = CodexUsageReader.GetIncompleteQuotaSnapshotDays(BackgroundCacheStart, lastHistoricalDay)
                .Select(day => DateOnly.FromDateTime(day.DateTime))
                .ToHashSet();
            var pendingTimeline = CodexUsageReader.GetIncompleteQuotaTimelineDays(BackgroundCacheStart, lastHistoricalDay)
                .Select(day => DateOnly.FromDateTime(day.DateTime))
                .ToHashSet();
            var timelineDays = pendingTimeline
                .Concat(pending[UsageSource.Codex])
                .Concat(pendingQuota)
                .ToHashSet();
            var total = pending.Values.Sum(days => days.Count) + pendingQuota.Count + timelineDays.Count;
            if (total == 0)
            {
                setStatus($"缓存完成 {DateTime.Now:HH:mm:ss}");
                return;
            }

            var completed = 0;
            setStatus($"缓存剩余 {total} 项");
            if (pendingQuota.Count > 0)
            {
                await WaitForForegroundAsync(token);
                setStatus($"缓存额度 {pendingQuota.Count} 项");
                var quotaDays = pendingQuota
                    .Select(date => new DateTimeOffset(date.Year, date.Month, date.Day, 0, 0, 0, CodexUsageReader.BeijingOffset))
                    .ToList();
                await RunExclusiveAsync(() => CodexUsageReader.WarmQuotaSnapshotDays(quotaDays, token), token);
                completed += pendingQuota.Count;
                setStatus($"缓存剩余 {total - completed} 项");
            }

            for (var day = lastHistoricalDay; day >= BackgroundCacheStart; day = day.AddDays(-1))
            {
                var date = DateOnly.FromDateTime(day.DateTime);
                foreach (var source in sources)
                {
                    if (!pending[source].Contains(date))
                    {
                        continue;
                    }

                    var reader = UsageSourceReaders.For(source);
                    await WaitForForegroundAsync(token);
                    setStatus($"缓存剩余 {total - completed} · {reader.Title} {day:MM-dd}");
                    await RunExclusiveAsync(() => reader.WarmHistoricalDay(day), token);
                    completed++;
                    await Task.Delay(20, token);
                }

                if (timelineDays.Contains(date))
                {
                    await WaitForForegroundAsync(token);
                    setStatus($"缓存剩余 {total - completed} · 额度明细 {day:MM-dd}");
                    await RunExclusiveAsync(() => CodexUsageReader.WarmQuotaTimelineDay(day), token);
                    completed++;
                    await Task.Delay(20, token);
                }
            }

            setStatus($"缓存完成 {DateTime.Now:HH:mm:ss}");
        }
        catch (OperationCanceledException)
        {
            // Foreground refreshes cancel warming. The next timer pass resumes from cache state.
        }
        catch (Exception ex)
        {
            setStatus($"后台缓存暂停：{ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(cts, localCts))
            {
                cts = null;
            }

            isRunning = false;
            if (!disposed)
            {
                timer.Start();
            }
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Stop();
        timer.Tick -= Timer_Tick;
    }

    private async void Timer_Tick(object? sender, EventArgs e)
    {
        await WarmNowAsync();
    }

    private UsageSource[] GetSourceOrder()
    {
        var current = currentSource();
        var sources = new[] { UsageSource.Codex, UsageSource.ClaudeCode, UsageSource.ZCode, UsageSource.WorkBuddy };
        return sources
            .Where(source => source == current)
            .Concat(sources.Where(source => source != current))
            .ToArray();
    }

    private async Task WaitForForegroundAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        while (isForegroundBusy())
        {
            await Task.Delay(250, token);
        }
    }

    private async Task RunExclusiveAsync(Action action, CancellationToken token)
    {
        await workGate.WaitAsync(token);
        try
        {
            await Task.Run(action, token);
        }
        finally
        {
            workGate.Release();
        }
    }

    private static DateTimeOffset StartOfDay(DateTimeOffset value)
    {
        return new DateTimeOffset(value.Year, value.Month, value.Day, 0, 0, 0, value.Offset);
    }
}
