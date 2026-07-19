using System.Windows.Threading;

namespace CodexTokenMonitor;

internal sealed record CacheWarmCategoryStatus(
    string Key,
    string Name,
    int TotalDays,
    int CompletedDays)
{
    public int RemainingDays => Math.Max(0, TotalDays - CompletedDays);
}

internal sealed record CacheWarmStatus(
    bool IsRunning,
    string Phase,
    string Summary,
    string CurrentItem,
    string CurrentCategoryKey,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CurrentItemStartedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<CacheWarmCategoryStatus> Categories)
{
    public int TotalItems => Categories.Sum(item => item.TotalDays);

    public int CompletedItems => Categories.Sum(item => item.CompletedDays);

    public int RemainingItems => Math.Max(0, TotalItems - CompletedItems);

    public double ProgressPercent => TotalItems == 0 ? 100d : CompletedItems / (double)TotalItems * 100d;

    public static CacheWarmStatus Idle { get; } = new(
        false,
        "尚未开始",
        "缓存详情",
        "-",
        "",
        null,
        null,
        DateTimeOffset.Now,
        Array.Empty<CacheWarmCategoryStatus>());
}

internal sealed class BackgroundCacheWarmer : IDisposable
{
    private const int BackgroundCacheIntervalMs = 120_000;
    private const int BackfillBatchDays = 7;

    private const string CodexUsageKey = "usage-Codex";
    private const string CodexQuotaKey = "codex-quota";
    private const string CodexTimelineKey = "codex-timeline";

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
    private readonly Action<CacheWarmStatus> setStatus;
    private readonly DispatcherTimer timer = new();
    private readonly Dictionary<string, CacheWarmCategoryStatus> categories = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? cts;
    private bool isRunning;
    private bool disposed;
    private DateTimeOffset? startedAt;
    private DateTimeOffset? currentItemStartedAt;
    private string currentItem = "-";
    private string currentCategoryKey = "";

    public BackgroundCacheWarmer(
        Func<UsageSource> currentSource,
        Func<bool> isForegroundBusy,
        SemaphoreSlim workGate,
        Action<CacheWarmStatus> setStatus)
    {
        this.currentSource = currentSource;
        this.isForegroundBusy = isForegroundBusy;
        this.workGate = workGate;
        this.setStatus = setStatus;
        timer.Interval = TimeSpan.FromMilliseconds(BackgroundCacheIntervalMs);
        timer.Tick += Timer_Tick;
    }

    public event Action<CacheWarmStatus>? StatusChanged;

    public bool IsRunning => isRunning;

    public CacheWarmStatus CurrentStatus { get; private set; } = CacheWarmStatus.Idle;

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
        startedAt ??= now;
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
            var totalDays = (lastHistoricalDay - BackgroundCacheStart).Days + 1;
            InitializeCategories(totalDays, sources, pending, pendingQuota, timelineDays);

            if (CurrentStatus.RemainingItems == 0)
            {
                PublishComplete();
                return;
            }

            foreach (var source in sources)
            {
                await WarmUsageSourceAsync(source, pending[source], lastHistoricalDay, token);
                if (source != UsageSource.Codex)
                {
                    continue;
                }

                await WarmQuotaAsync(pendingQuota, lastHistoricalDay, token);
                await WarmTimelineAsync(timelineDays, lastHistoricalDay, token);
            }

            PublishComplete();
        }
        catch (OperationCanceledException)
        {
            Publish(
                "已暂停，等待前台查询完成",
                "缓存暂停 · 点击查看",
                isRunning: false);
        }
        catch (Exception ex)
        {
            Publish(
                $"后台缓存暂停：{ex.Message}",
                "缓存异常 · 点击查看",
                isRunning: false);
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

    private async Task WarmUsageSourceAsync(
        UsageSource source,
        ISet<DateOnly> pendingDays,
        DateTimeOffset lastHistoricalDay,
        CancellationToken token)
    {
        var reader = UsageSourceReaders.For(source);
        var categoryKey = UsageKey(source);
        for (var day = lastHistoricalDay; day >= BackgroundCacheStart; day = day.AddDays(-1))
        {
            var date = DateOnly.FromDateTime(day.DateTime);
            if (!pendingDays.Contains(date))
            {
                continue;
            }

            await WaitForForegroundAsync(token);
            PublishTask(categoryKey, $"{reader.Title} token {day:yyyy-MM-dd}");
            await RunExclusiveAsync(() => reader.WarmHistoricalDay(day), token);
            MarkCompleted(categoryKey);
            await Task.Delay(20, token);
        }
    }

    private async Task WarmQuotaAsync(
        ISet<DateOnly> pendingQuota,
        DateTimeOffset lastHistoricalDay,
        CancellationToken token)
    {
        var days = EnumeratePendingDays(lastHistoricalDay, pendingQuota).ToList();
        for (var index = 0; index < days.Count; index += BackfillBatchDays)
        {
            var batch = days.Skip(index).Take(BackfillBatchDays).OrderBy(day => day).ToList();
            await WaitForForegroundAsync(token);
            PublishTask(CodexQuotaKey, $"Codex 额度 {batch[0]:MM-dd} - {batch[^1]:MM-dd}");
            await RunExclusiveAsync(() => CodexUsageReader.WarmQuotaSnapshotDays(batch, token), token);
            MarkCompleted(CodexQuotaKey, batch.Count);
        }
    }

    private async Task WarmTimelineAsync(
        ISet<DateOnly> pendingTimeline,
        DateTimeOffset lastHistoricalDay,
        CancellationToken token)
    {
        foreach (var day in EnumeratePendingDays(lastHistoricalDay, pendingTimeline))
        {
            await WaitForForegroundAsync(token);
            PublishTask(CodexTimelineKey, $"Codex 额度曲线 {day:yyyy-MM-dd}");
            await RunExclusiveAsync(() => CodexUsageReader.WarmQuotaTimelineDay(day), token);
            MarkCompleted(CodexTimelineKey);
            await Task.Delay(20, token);
        }
    }

    private void InitializeCategories(
        int totalDays,
        IReadOnlyList<UsageSource> sources,
        IReadOnlyDictionary<UsageSource, HashSet<DateOnly>> pending,
        ISet<DateOnly> pendingQuota,
        ISet<DateOnly> pendingTimeline)
    {
        categories.Clear();
        foreach (var source in sources)
        {
            var reader = UsageSourceReaders.For(source);
            categories[UsageKey(source)] = new CacheWarmCategoryStatus(
                UsageKey(source),
                $"{reader.Title} token",
                totalDays,
                totalDays - pending[source].Count);
        }

        categories[CodexQuotaKey] = new CacheWarmCategoryStatus(
            CodexQuotaKey,
            "Codex 额度快照",
            totalDays,
            totalDays - pendingQuota.Count);
        categories[CodexTimelineKey] = new CacheWarmCategoryStatus(
            CodexTimelineKey,
            "Codex 额度曲线",
            totalDays,
            totalDays - pendingTimeline.Count);
        Publish("正在整理缓存队列", "缓存准备中 · 点击查看", isRunning: true);
    }

    private void PublishTask(string categoryKey, string item)
    {
        currentCategoryKey = categoryKey;
        if (!string.Equals(currentItem, item, StringComparison.Ordinal))
        {
            currentItem = item;
            currentItemStartedAt = DateTimeOffset.Now;
        }

        var category = categories[categoryKey];
        Publish(
            $"正在缓存 {item}",
            $"缓存 {category.Name} {category.CompletedDays}/{category.TotalDays}天 · 点击查看",
            isRunning: true);
    }

    private void MarkCompleted(string categoryKey, int count = 1)
    {
        var category = categories[categoryKey];
        categories[categoryKey] = category with
        {
            CompletedDays = Math.Min(category.TotalDays, category.CompletedDays + count)
        };
        PublishTask(categoryKey, currentItem);
    }

    private void PublishComplete()
    {
        currentItem = "-";
        currentCategoryKey = "";
        currentItemStartedAt = null;
        Publish(
            $"全部缓存已完成 {DateTime.Now:HH:mm:ss}",
            $"缓存完成 {DateTime.Now:HH:mm:ss} · 点击查看",
            isRunning: false);
    }

    private void Publish(string phase, string summary, bool isRunning)
    {
        CurrentStatus = new CacheWarmStatus(
            isRunning,
            phase,
            summary,
            currentItem,
            currentCategoryKey,
            startedAt,
            currentItemStartedAt,
            DateTimeOffset.Now,
            categories.Values
                .OrderBy(item => CategoryOrder(item.Key))
                .ToList());
        setStatus(CurrentStatus);
        StatusChanged?.Invoke(CurrentStatus);
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
            .Where(source => source == UsageSource.Codex)
            .Concat(sources.Where(source => source == current && source != UsageSource.Codex))
            .Concat(sources.Where(source => source != UsageSource.Codex && source != current))
            .ToArray();
    }

    private async Task WaitForForegroundAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (isForegroundBusy())
        {
            Publish(
                $"等待前台查询完成 · {currentItem}",
                "缓存等待前台查询 · 点击查看",
                isRunning: true);
        }

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

    private static IEnumerable<DateTimeOffset> EnumeratePendingDays(
        DateTimeOffset lastHistoricalDay,
        ISet<DateOnly> pending)
    {
        for (var day = lastHistoricalDay; day >= BackgroundCacheStart; day = day.AddDays(-1))
        {
            if (pending.Contains(DateOnly.FromDateTime(day.DateTime)))
            {
                yield return day;
            }
        }
    }

    private static string UsageKey(UsageSource source) => $"usage-{source}";

    private static int CategoryOrder(string key)
    {
        return key switch
        {
            CodexUsageKey => 0,
            CodexQuotaKey => 1,
            CodexTimelineKey => 2,
            "usage-ClaudeCode" => 3,
            "usage-ZCode" => 4,
            "usage-WorkBuddy" => 5,
            _ => 10
        };
    }

    private static DateTimeOffset StartOfDay(DateTimeOffset value)
    {
        return new DateTimeOffset(value.Year, value.Month, value.Day, 0, 0, 0, value.Offset);
    }
}
