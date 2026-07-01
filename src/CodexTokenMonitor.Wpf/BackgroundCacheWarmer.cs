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
    private readonly Action<string> setStatus;
    private readonly DispatcherTimer timer = new();
    private CancellationTokenSource? cts;
    private bool isRunning;
    private bool disposed;

    public BackgroundCacheWarmer(
        Func<UsageSource> currentSource,
        Func<bool> isForegroundBusy,
        Action<string> setStatus)
    {
        this.currentSource = currentSource;
        this.isForegroundBusy = isForegroundBusy;
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
            var total = pending.Values.Sum(days => days.Count) + pendingQuota.Count;
            if (total == 0)
            {
                setStatus($"缓存完成 {DateTime.Now:HH:mm:ss}");
                return;
            }

            var completed = 0;
            setStatus($"缓存 0/{total}");
            for (var day = lastHistoricalDay; day >= BackgroundCacheStart; day = day.AddDays(-1))
            {
                var date = DateOnly.FromDateTime(day.DateTime);
                if (pendingQuota.Contains(date))
                {
                    await WaitForForegroundAsync(token);
                    setStatus($"缓存 {completed + 1}/{total} 额度 {day:MM-dd}");
                    await Task.Run(() => CodexUsageReader.WarmQuotaSnapshotDay(day), token);
                    completed++;
                    await Task.Delay(20, token);
                }

                foreach (var source in sources)
                {
                    if (!pending[source].Contains(date))
                    {
                        continue;
                    }

                    var reader = UsageSourceReaders.For(source);
                    await WaitForForegroundAsync(token);
                    setStatus($"缓存 {completed + 1}/{total} {reader.Title} {day:MM-dd}");
                    await Task.Run(() => reader.WarmHistoricalDay(day), token);
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
        var sources = new[] { UsageSource.Codex, UsageSource.ClaudeCode, UsageSource.ZCode };
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

    private static DateTimeOffset StartOfDay(DateTimeOffset value)
    {
        return new DateTimeOffset(value.Year, value.Month, value.Day, 0, 0, 0, value.Offset);
    }
}
