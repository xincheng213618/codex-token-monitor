using System.Diagnostics;
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
        BeijingClock.Now,
        Array.Empty<CacheWarmCategoryStatus>());
}

internal sealed class BackgroundCacheWarmer : IDisposable
{
    private const int BackgroundCacheIntervalMs = 120_000;

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
    private readonly MonitorRuntime? runtime;
    private readonly DispatcherTimer timer = new();
    private readonly Dictionary<string, CacheWarmCategoryStatus> categories = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? cts;
    private bool isRunning;
    private bool disposed;
    private DateTimeOffset? startedAt;
    private DateTimeOffset? currentItemStartedAt;
    private string currentItem = "-";
    private string currentCategoryKey = "";
    private Task currentWarmTask = Task.CompletedTask;

    public BackgroundCacheWarmer(
        Func<UsageSource> currentSource,
        Func<bool> isForegroundBusy,
        SemaphoreSlim workGate,
        Action<CacheWarmStatus> setStatus,
        MonitorRuntime? runtime = null)
    {
        this.currentSource = currentSource;
        this.isForegroundBusy = isForegroundBusy;
        this.workGate = workGate;
        this.setStatus = setStatus;
        this.runtime = runtime;
        timer.Interval = TimeSpan.FromMilliseconds(BackgroundCacheIntervalMs);
        timer.Tick += Timer_Tick;
    }

    public event Action<CacheWarmStatus>? StatusChanged;

    public bool IsRunning => isRunning;

    public Task Completion => currentWarmTask;

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
        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // A completed warm cycle may be disposing its source concurrently.
        }
    }

    public Task WarmNowAsync()
    {
        if (isRunning || disposed || runtime?.IsStopping == true)
        {
            return Task.CompletedTask;
        }

        currentWarmTask = runtime is null
            ? WarmNowCoreAsync()
            : runtime.Run("历史缓存预热", _ => WarmNowCoreAsync());
        return currentWarmTask;
    }

    private async Task WarmNowCoreAsync()
    {
        using var diagnostics = CacheOperationDiagnostics.Begin();
        var now = DateTimeOffset.UtcNow.ToOffset(CodexUsageReader.BeijingOffset);
        var lastHistoricalDay = StartOfDay(now).AddDays(-1);
        if (lastHistoricalDay < BackgroundCacheStart)
        {
            return;
        }

        CancelCurrent();
        var localCts = CancellationTokenSource.CreateLinkedTokenSource(runtime?.LifetimeToken ?? CancellationToken.None);
        cts = localCts;
        var token = localCts.Token;
        isRunning = true;
        startedAt = now;
        currentItem = "-";
        currentCategoryKey = "";
        currentItemStartedAt = null;
        timer.Stop();

        try
        {
            var sources = GetSourceOrder();
            await WaitForForegroundAsync(token);
            var plan = await BuildWarmPlanAsync(sources, lastHistoricalDay, token);
            var totalDays = (lastHistoricalDay - BackgroundCacheStart).Days + 1;
            InitializeCategories(
                totalDays,
                plan.Sources,
                plan.Pending,
                plan.PendingQuota,
                plan.TimelineDays);

            if (CurrentStatus.RemainingItems == 0)
            {
                PublishComplete();
                return;
            }

            foreach (var source in plan.Sources)
            {
                await WarmUsageSourceAsync(source, plan.Pending[source], lastHistoricalDay, token);
                if (source != UsageSource.Codex)
                {
                    continue;
                }

                await WarmQuotaAsync(plan.PendingQuota, lastHistoricalDay, token);
                await WarmTimelineAsync(plan.TimelineDays, lastHistoricalDay, token);
            }

            if (CurrentStatus.RemainingItems == 0)
            {
                PublishComplete();
            }
            else
            {
                PublishPendingRetry();
            }
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

            localCts.Dispose();

            isRunning = false;
            if (!disposed)
            {
                if (!token.IsCancellationRequested && diagnostics.Warnings.Count > 0)
                {
                    Publish($"缓存读写失败：{diagnostics.Warnings[0].Message}", "缓存异常 · 点击查看", isRunning: false);
                }
                timer.Start();
            }
        }
    }

    private async Task<WarmPlan> BuildWarmPlanAsync(
        IReadOnlyList<UsageSource> sources,
        DateTimeOffset lastHistoricalDay,
        CancellationToken token)
    {
        await workGate.WaitAsync(token);
        try
        {
            return await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                var pending = sources.ToDictionary(
                    source => source,
                    source => UsageSourceRegistry.For(source).CachedQueries
                        .GetIncompleteHistoricalDays(BackgroundCacheStart, lastHistoricalDay, token)
                        .Select(day => DateOnly.FromDateTime(day.DateTime))
                        .ToHashSet());
                var pendingQuota = CodexUsageReader.GetIncompleteQuotaSnapshotDays(
                        BackgroundCacheStart,
                        lastHistoricalDay,
                        token)
                    .Select(day => DateOnly.FromDateTime(day.DateTime))
                    .ToHashSet();
                var pendingTimeline = CodexUsageReader.GetIncompleteQuotaTimelineDays(
                        BackgroundCacheStart,
                        lastHistoricalDay,
                        token)
                    .Select(day => DateOnly.FromDateTime(day.DateTime))
                    .ToHashSet();
                var timelineDays = pendingTimeline
                    .Concat(pending[UsageSource.Codex])
                    .Concat(pendingQuota)
                    .ToHashSet();

                return new WarmPlan(sources, pending, pendingQuota, timelineDays);
            }, token);
        }
        finally
        {
            workGate.Release();
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
        var definition = UsageSourceRegistry.For(source);
        var maintenance = definition.CacheMaintenance;
        var categoryKey = UsageKey(source);
        var days = EnumeratePendingDays(lastHistoricalDay, pendingDays).ToArray();
        if (days.Length == 0)
        {
            return;
        }

        await WaitForForegroundAsync(token);
        PublishTask(categoryKey, $"{definition.Title} token 批量读取 {days.Length} 天");
        var progressClock = Stopwatch.StartNew();
        await RunExclusiveAsync(() => maintenance.WarmHistoricalDays(
            days,
            token,
            day => DispatchStatus(() =>
            {
                PublishTask(categoryKey, $"{definition.Title} token {day:yyyy-MM-dd}");
                MarkCompleted(categoryKey);
            }),
            (completed, total) =>
            {
                if (completed != total && completed != 0 && progressClock.ElapsedMilliseconds < 500)
                {
                    return;
                }

                progressClock.Restart();
                DispatchStatus(() => PublishTask(
                    categoryKey,
                    $"{definition.Title} token 读取日志 {completed:N0}/{total:N0} · 合并 {days.Length} 天"));
            }), token);
    }

    private void DispatchStatus(Action update)
    {
        if (disposed || timer.Dispatcher.HasShutdownStarted)
        {
            return;
        }

        if (timer.Dispatcher.CheckAccess())
        {
            update();
        }
        else
        {
            timer.Dispatcher.Invoke(update);
        }
    }

    private async Task WarmQuotaAsync(
        ISet<DateOnly> pendingQuota,
        DateTimeOffset lastHistoricalDay,
        CancellationToken token)
    {
        var days = EnumeratePendingDays(lastHistoricalDay, pendingQuota).ToList();
        if (days.Count == 0)
        {
            return;
        }

        await WaitForForegroundAsync(token);
        PublishTask(CodexQuotaKey, $"Codex 额度合并扫描 {days.Count} 天");
        var progressClock = Stopwatch.StartNew();
        await RunExclusiveAsync(() => CodexUsageReader.WarmQuotaSnapshotDays(
            days, token,
            day => DispatchStatus(() =>
            {
                PublishTask(CodexQuotaKey, $"Codex 额度 {day:yyyy-MM-dd}");
                MarkCompleted(CodexQuotaKey);
            }),
            (completed, total) =>
            {
                if (completed != 0 && completed != total && progressClock.ElapsedMilliseconds < 500)
                {
                    return;
                }
                progressClock.Restart();
                DispatchStatus(() => PublishTask(CodexQuotaKey,
                    $"Codex 额度读取日志 {completed:N0}/{total:N0} · 合并 {days.Count} 天"));
            }), token);
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
            await RunExclusiveAsync(() => CodexUsageReader.WarmQuotaTimelineDay(day, token), token);
            if (CodexUsageReader.GetIncompleteQuotaTimelineDays(day, day, token).Count == 0)
            {
                MarkCompleted(CodexTimelineKey);
            }
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
            var definition = UsageSourceRegistry.For(source);
            categories[UsageKey(source)] = new CacheWarmCategoryStatus(
                UsageKey(source),
                $"{definition.Title} token",
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
            currentItemStartedAt = BeijingClock.Now;
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
            $"全部缓存已完成 {BeijingClock.DateTimeNow:HH:mm:ss}",
            $"缓存完成 {BeijingClock.DateTimeNow:HH:mm:ss} · 点击查看",
            isRunning: false);
    }

    private void PublishPendingRetry()
    {
        var remaining = CurrentStatus.RemainingItems;
        currentItem = "-";
        currentCategoryKey = "";
        currentItemStartedAt = null;
        Publish(
            $"本轮预热结束，仍待补扫 {remaining} 天",
            $"缓存待重试 {remaining} 天 · 下一轮自动重试",
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
            BeijingClock.Now,
            categories.Values
                .OrderBy(item => CategoryOrder(item.Key))
                .ToList());

        // Status is auxiliary UI telemetry. A window can be torn down while
        // the cache worker is unwinding cancellation; a dispatcher callback
        // from that window must not turn a normal shutdown into a faulted
        // background task.
        try
        {
            setStatus(CurrentStatus);
        }
        catch
        {
            // Keep cache work alive even if its status consumer has already
            // lost its dispatcher during application shutdown.
        }

        try
        {
            StatusChanged?.Invoke(CurrentStatus);
        }
        catch
        {
            // Status observers are optional and must never break cache work.
        }
    }

    private async void Timer_Tick(object? sender, EventArgs e)
    {
        await WarmNowAsync();
    }

    private UsageSource[] GetSourceOrder()
    {
        var current = currentSource();
        var sources = UsageSourceRegistry.All.Select(definition => definition.Source).ToArray();
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
        var local = value.ToOffset(CodexUsageReader.BeijingOffset);
        return new DateTimeOffset(local.Year, local.Month, local.Day, 0, 0, 0, CodexUsageReader.BeijingOffset);
    }

    private sealed record WarmPlan(
        IReadOnlyList<UsageSource> Sources,
        IReadOnlyDictionary<UsageSource, HashSet<DateOnly>> Pending,
        HashSet<DateOnly> PendingQuota,
        HashSet<DateOnly> TimelineDays);
}
