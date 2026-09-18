using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexTokenMonitor;
using Microsoft.Data.Sqlite;

namespace CodexTokenMonitor.Wpf.Probes;

internal static class AnalysisProbe
{
    private static readonly List<object> Results = new();
    private static readonly List<Window> Windows = new();
    private static readonly HashSet<Window> ClosedWindows = new();
    private static string outputRoot = "";
    private static string isolatedRoot = "";
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);
    private static MonitorRuntime? suiteRuntime;

    internal static void Run(string output)
    {
        outputRoot = output;
        isolatedRoot = Path.Combine(outputRoot, "isolated-" + Guid.NewGuid().ToString("N"));
        var logRoot = Path.Combine(isolatedRoot, "logs");
        using var caches = MonitorCachePaths.PushLocalAppDataRoot(isolatedRoot);
        using var logs = UsageLogPaths.PushRoot(logRoot);
        Directory.CreateDirectory(logRoot);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/CodexTokenMonitor;component/Themes/MonitorTheme.xaml", UriKind.Relative)
        });
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            Exception? failure = null;
            try
            {
                await RunAsync();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            foreach (var window in Windows.Where(window => !ClosedWindows.Contains(window)).Reverse().ToArray())
            {
                try { await CloseAsync(window); }
                catch (Exception ex) { failure = failure is null ? ex : new AggregateException(failure, ex); }
            }
            try
            {
                if (suiteRuntime is not null)
                    Require((await suiteRuntime.StopAsync(Budget)).Completed, "cleanup drains the suite runtime");
            }
            catch (Exception ex) { failure = failure is null ? ex : new AggregateException(failure, ex); }
            Environment.ExitCode = failure is null ? 0 : 1;
            try { WriteReport(failure is null, failure?.ToString()); }
            finally { app.Shutdown(Environment.ExitCode); }
        }));
        app.Run();
    }

    private static async Task RunAsync()
    {
        var logRoot = Path.Combine(isolatedRoot, "logs");
        Require(Path.GetFullPath(MonitorCachePaths.LocalAppData) == isolatedRoot, "isolated application cache root");
        foreach (var source in Enum.GetValues<UsageSource>())
            Require(UsageLogPaths.GetOverrideRoot(source)!.StartsWith(logRoot, StringComparison.OrdinalIgnoreCase), "isolated log root");

        // The shared reader instance owns the attempt timestamp now that the
        // app-server quota reader is instance-scoped.
        var appServer = UsageSourceReaders.Codex.AppServerQuota;
        var appServerAttempt = appServer.GetType().GetField("lastAttemptUtc", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var attemptBefore = appServerAttempt.GetValue(appServer);
        var (quota, periods) = SeedCaches();
        using var runtime = new MonitorRuntime();
        suiteRuntime = runtime;

        await CheckCachedWindowsAndResolvedPeriodsAsync(runtime, quota, periods);
        await CheckWindowCancellationAsync(runtime, periods[1]);
        await CheckCycleFaultAndRecoveryAsync(runtime, periods[1], periods[0]);
        await CheckParentShutdownAsync(periods[1]);

        var stopped = await runtime.StopAsync(Budget);
        Require(stopped.Completed, "primary probe runtime drains");
        Require(Equals(attemptBefore, appServerAttempt.GetValue(appServer)), "no app-server quota requests");
        Require(!Directory.EnumerateFiles(logRoot, "*", SearchOption.AllDirectories).Any(), "source log fixtures remain empty");
        Require(Windows.All(ClosedWindows.Contains), "all shown windows reached real Closed");
        Results.Add(new { check = "isolation-and-exit", isolatedRoot, logRoot, mainWindowConstructed = false,
            appServerReads = 0, sharingServerStarted = false, windowsShown = Windows.Count, allClosed = true });
    }

    private static async Task CheckCachedWindowsAndResolvedPeriodsAsync(
        MonitorRuntime runtime, CodexQuotaEstimate quota, IReadOnlyList<CodexQuotaCycle> periods)
    {
        var timelineBefore = TimelineCount();
        Require(timelineBefore == 0, "fixture begins without materialized quota anchors");
        var curve = CreateWindow(typeof(QuotaCostCurveWindow), quota, periods, runtime);
        var estimate = CreateWindow(typeof(QuotaEstimateWindow), quota, Array.Empty<CodexQuotaCycle>(), runtime);
        await runtime.SharedIoGate.WaitAsync();
        try
        {
            await ShowAsync(curve);
            await ShowAsync(estimate);
            await WaitUntilAsync(() => !Get<bool>(curve, "isLoading") && !Get<bool>(estimate, "isLoading"), "cached windows finish behind held parent gate");
            var curveResult = Get<QuotaCostCurveResult>(curve, "loadedResult");
            var estimateResult = Get<QuotaEstimateLoadResult>(estimate, "loadedEstimateResult");
            Require(curveResult.Curves.Count > 0 && curveResult.Curves.Sum(item => item.Points.Count) > 3,
                "read-only curve projects missing anchors into memory");
            Require(estimateResult.WeeklyRows.Count >= 2, "estimate displays synthesized historical periods");
            Require(TimelineCount() == timelineBefore, "cached windows do not materialize database timeline rows");
            Require(ReferenceEquals(Get<AnalysisQuerySession>(curve, "querySession").Runtime, runtime), "curve shares parent runtime");
            Require(ReferenceEquals(Get<AnalysisQuerySession>(estimate, "querySession").Runtime, runtime), "estimate shares parent runtime");
            Results.Add(new { check = "cached-windows-bypass-gate", curveCount = curveResult.Curves.Count,
                curvePoints = curveResult.Curves.Sum(item => item.Points.Count), weeklyRows = estimateResult.WeeklyRows.Count,
                timelineBefore, timelineAfter = TimelineCount() });

            Invoke(estimate, "QuotaCurveButton_Click", estimate, new RoutedEventArgs());
            await WaitUntilAsync(() => estimate.OwnedWindows.Cast<Window>().Any(window => window is QuotaCostCurveWindow),
                "estimate opens the quota curve on demand");
            var onDemandCurve = estimate.OwnedWindows.Cast<Window>().Single(window => window is QuotaCostCurveWindow);
            RegisterWindow(onDemandCurve);
            onDemandCurve.Left = onDemandCurve.Top = -20000;
            onDemandCurve.ShowInTaskbar = false;
            await WaitUntilAsync(() => !Get<bool>(onDemandCurve, "isLoading"), "on-demand curve finishes loading");
            Require(ReferenceEquals(Get<AnalysisQuerySession>(onDemandCurve, "querySession").Runtime, runtime),
                "on-demand curve shares the estimate runtime");
            Require(Get<QuotaCostCurveResult>(onDemandCurve, "loadedResult").Curves.Count > 0,
                "on-demand curve receives cached data");
            await CloseAsync(onDemandCurve);
            Results.Add(new { check = "estimate-opens-curve-on-demand", embeddedCurveLoaded = false,
                childRuntimeShared = true });

            // The constructor received no periods. The right-click child must
            // use the actual successfully loaded period set, including its predecessor.
            var loadedPeriods = Get<IReadOnlyList<CodexQuotaCycle>>(estimate, "loadedWeeklyPeriods");
            Require(estimateResult.Periods.Count >= 2 && loadedPeriods.Count == estimateResult.Periods.Count,
                "estimate retains resolved fallback periods");
            var selected = loadedPeriods.Where(item => !item.IsCurrent && loadedPeriods.Any(previous => previous.PeriodStart < item.PeriodStart))
                .OrderByDescending(item => item.PeriodStart).First();
            var previous = loadedPeriods.Where(item => item.PeriodStart < selected.PeriodStart).OrderByDescending(item => item.PeriodStart).First();
            var grid = Get<DataGrid>(estimate, "WeeklyGrid");
            grid.SelectedItem = estimateResult.WeeklyRows.Single(row => row.PeriodStart == selected.PeriodStart);
            Invoke(estimate, "AnalyzeCycleMenuItem_Click", grid, new RoutedEventArgs());
            await WaitUntilAsync(() => estimate.OwnedWindows.Cast<Window>().Any(window => window is QuotaCycleAnalysisWindow), "right-click creates cycle analysis child");
            var child = estimate.OwnedWindows.Cast<Window>().Single(window => window is QuotaCycleAnalysisWindow);
            RegisterWindow(child);
            child.Left = child.Top = -20000;
            child.ShowInTaskbar = false;
            Require(Get<CodexQuotaCycle>(child, "previousPeriod").PeriodStart == previous.PeriodStart, "fallback child receives predecessor");
            Require(ReferenceEquals(Get<AnalysisQuerySession>(child, "querySession").Runtime, runtime), "derived analysis shares same application runtime");
            await CloseAsync(child);
            Results.Add(new { check = "empty-known-period-fallback", loadedPeriods = loadedPeriods.Count,
                selectedStart = selected.PeriodStart, previousStart = previous.PeriodStart, childRuntimeShared = true });
        }
        finally
        {
            runtime.SharedIoGate.Release();
        }
        await CheckCachedWindowFaultAndRecoveryAsync(curve, estimate);
        await RenderAsync(curve, "quota-cost-curve.png");
        await RenderAsync(estimate, "quota-estimate.png");
        await CloseAsync(curve);
        await CloseAsync(estimate);
    }

    private static async Task CheckCachedWindowFaultAndRecoveryAsync(Window curve, Window estimate)
    {
        var curveBefore = Get<QuotaCostCurveResult>(curve, "loadedResult");
        var estimateBefore = Get<QuotaEstimateLoadResult>(estimate, "loadedEstimateResult");
        var grid = Get<DataGrid>(estimate, "WeeklyGrid");
        var rowsBefore = grid.ItemsSource;
        Require(curveBefore.Curves.Count == 3 && estimateBefore.PeriodCount == 3 && grid.Items.Count == 3,
            "cached window fixture has three successful periods before corruption");
        await RunManualEstimateAsync(estimate);
        var manualBefore = Get<string>(estimate, "lastManualResult");
        Require(!string.IsNullOrWhiteSpace(manualBefore) && Get<TextBlock>(estimate, "ManualResultText").ToolTip is null,
            "manual estimate establishes a successful baseline");

        var path = FixtureDatabasePath();
        var backup = path + ".cached-windows-healthy-backup";
        Checkpoint(path);
        SqliteConnection.ClearAllPools();
        File.Copy(path, backup, overwrite: true);
        string curveFaultStatus;
        string estimateFaultStatus;
        string manualFaultStatus;
        try
        {
            File.WriteAllText(path, "Deliberately corrupt isolated SQLite fixture for cached window preservation.");
            await Task.WhenAll(InvokeTask(curve, "LoadAsync"), InvokeTask(estimate, "LoadRowsAsync")).WaitAsync(Budget);
            curveFaultStatus = Get<TextBlock>(curve, "StatusText").Text;
            estimateFaultStatus = Get<TextBlock>(estimate, "StatusText").Text;
            Require(IsFailureText(curveFaultStatus) && IsFailureText(estimateFaultStatus), "both cached windows report actual database failure");
            Require(Get<TextBlock>(curve, "StatusText").ToolTip is not null && Get<TextBlock>(estimate, "StatusText").ToolTip is not null,
                "both cached windows expose cache fault diagnostics");
            Require(ReferenceEquals(curveBefore, GetObject(curve, "loadedResult")), "cost curve preserves the successful result object");
            Require(ReferenceEquals(estimateBefore, GetObject(estimate, "loadedEstimateResult")) && ReferenceEquals(rowsBefore, grid.ItemsSource),
                "estimate preserves the successful result and table objects");

            await RunManualEstimateAsync(estimate);
            manualFaultStatus = Get<TextBlock>(estimate, "ManualResultText").Text;
            Require(Get<string>(estimate, "lastManualResult") == manualBefore && manualFaultStatus.StartsWith(manualBefore, StringComparison.Ordinal),
                "manual cache failure retains the previous successful estimate");
            Require(IsFailureText(manualFaultStatus) && Get<TextBlock>(estimate, "ManualResultText").ToolTip is not null,
                "manual cache failure adds warning and diagnostics to retained result");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Copy(backup, path, overwrite: true);
        }

        await Task.WhenAll(InvokeTask(curve, "LoadAsync"), InvokeTask(estimate, "LoadRowsAsync")).WaitAsync(Budget);
        var recoveredCurve = Get<QuotaCostCurveResult>(curve, "loadedResult");
        var recoveredEstimate = Get<QuotaEstimateLoadResult>(estimate, "loadedEstimateResult");
        Require(recoveredCurve.Curves.Count == 3 && recoveredEstimate.PeriodCount == 3 && grid.Items.Count == 3,
            "restored cached windows retain all three periods");
        Require(!ReferenceEquals(curveBefore, recoveredCurve) && !ReferenceEquals(estimateBefore, recoveredEstimate),
            "restored cached windows publish fresh successful results");
        Require(Get<TextBlock>(curve, "StatusText").ToolTip is null && Get<TextBlock>(estimate, "StatusText").ToolTip is null,
            "restored cached windows clear prior failure tooltips");
        Require(!IsFailureText(Get<TextBlock>(curve, "StatusText").Text) && !IsFailureText(Get<TextBlock>(estimate, "StatusText").Text),
            "restored cached windows clear prior failure statuses");

        await RunManualEstimateAsync(estimate);
        Require(Get<string>(estimate, "lastManualResult") == manualBefore && Get<TextBlock>(estimate, "ManualResultText").Text == manualBefore,
            "manual estimate retry restores the same successful result text");
        Require(Get<TextBlock>(estimate, "ManualResultText").ToolTip is null, "manual estimate recovery clears prior diagnostics");
        Results.Add(new { check = "cached-windows-corruption-recovery", curveFaultStatus, estimateFaultStatus,
            successfulObjectsPreservedDuringFailure = true, recoveredPeriods = 3, tooltipsCleared = true });
        Results.Add(new { check = "manual-estimate-corruption-recovery", manualFaultStatus,
            previousResultPreserved = true, recoveredResultMatches = true, tooltipCleared = true });
    }

    private static async Task RunManualEstimateAsync(Window estimate)
    {
        Invoke(estimate, "ManualEstimateButton_Click", Get<Button>(estimate, "ManualEstimateButton"), new RoutedEventArgs());
        await WaitUntilAsync(() => !Get<bool>(estimate, "isManualEstimating"), "manual estimate operation completes");
    }

    private static bool IsFailureText(string text) => text.Contains("失败", StringComparison.Ordinal) || text.Contains("不可用", StringComparison.Ordinal);

    private static async Task CheckWindowCancellationAsync(MonitorRuntime runtime, CodexQuotaCycle period)
    {
        var window = CreateWindow(typeof(QuotaCycleAnalysisWindow), period, null, null, runtime);
        var session = Get<AnalysisQuerySession>(window, "querySession");
        await runtime.SharedIoGate.WaitAsync();
        try
        {
            await ShowAsync(window);
            Require(Get<bool>(window, "analysisLoading") && LocalOperationCount(session) > 0, "analysis waits for parent shared gate");
            var answered = false;
            await window.Dispatcher.InvokeAsync(() => answered = true, DispatcherPriority.Background);
            Require(answered, "dispatcher responds while analysis waits");
            await CloseAsync(window);
            await WaitUntilAsync(() => LocalOperationCount(session) == 0, "closed window query drains");
            Require(session.IsStopping && !runtime.IsStopping, "closing a window does not stop application runtime");
            await runtime.Run("probe parent remains usable", _ => Task.CompletedTask);
            Results.Add(new { check = "single-window-close", dispatcherResponsive = true, queryDrained = true, parentStillUsable = true });
        }
        finally
        {
            runtime.SharedIoGate.Release();
        }
    }

    private static async Task CheckCycleFaultAndRecoveryAsync(MonitorRuntime runtime, CodexQuotaCycle period, CodexQuotaCycle previous)
    {
        var window = CreateWindow(typeof(QuotaCycleAnalysisWindow), period, null, previous, runtime);
        await ShowAsync(window);
        await WaitUntilAsync(() => !Get<bool>(window, "analysisLoading"), "cycle analysis completes");
        Require(Get<bool>(window, "hasSuccessfulResult"), "cycle starts with successful result");
        var grid = Get<DataGrid>(window, "BandGrid");
        var rowsBefore = grid.ItemsSource;
        var chart = GetObject(window, "chart")!;
        var analysisBefore = Get<QuotaCycleAnalysisResult>(chart, "result");
        Require(analysisBefore.HasData && grid.Items.Count > 0, "cycle fixture produces plotted bands");
        var countBefore = grid.Items.Count;
        var totalBefore = Get<TextBlock>(window, "EquivalentCostValue").Text;
        var path = FixtureDatabasePath();
        var backup = path + ".healthy-backup";
        Checkpoint(path);
        SqliteConnection.ClearAllPools();
        File.Copy(path, backup, overwrite: true);
        string faultStatus;
        try
        {
            File.WriteAllText(path, "Deliberately corrupt isolated SQLite fixture for round 3.");
            await InvokeTask(window, "LoadAnalysisAsync").WaitAsync(Budget);
            faultStatus = Get<TextBlock>(window, "StatusText").Text;
            Require(faultStatus.Contains("失败", StringComparison.Ordinal) || faultStatus.Contains("不可用", StringComparison.Ordinal), "cycle cache fault is visible");
            Require(Get<TextBlock>(window, "StatusText").ToolTip is not null, "cycle warning contains diagnostics");
            Require(ReferenceEquals(rowsBefore, grid.ItemsSource) && ReferenceEquals(analysisBefore, GetObject(chart, "result")), "cache failure preserves identical table and chart data");
            Require(Get<TextBlock>(window, "EquivalentCostValue").Text == totalBefore, "cache failure preserves summary value");
            await RenderAsync(window, "cycle-cache-failure-preserved.png");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Copy(backup, path, overwrite: true);
        }

        await InvokeTask(window, "LoadAnalysisAsync").WaitAsync(Budget);
        var recovered = Get<QuotaCycleAnalysisResult>(chart, "result");
        Require(recovered.HasData && grid.Items.Count == countBefore && recovered.Tokens == analysisBefore.Tokens, "restored database refresh recovers statistics");
        Require(!ReferenceEquals(analysisBefore, recovered), "successful retry publishes a new result");
        Require(Get<TextBlock>(window, "StatusText").ToolTip is null, "recovery clears diagnostic tooltip");
        Require(Get<TextBlock>(window, "ModelCapacityValue").Text.Contains("本期稳健回归", StringComparison.Ordinal),
            "cycle labels current-only robust regression accurately");
        await RenderAsync(window, "cycle-analysis-recovered.png");
        Results.Add(new { check = "cycle-corruption-recovery", faultStatus, preservedBandCount = countBefore,
            recoveredTokens = recovered.Tokens, tooltipCleared = true, sameTableAndChartPreservedDuringFailure = true });
        await CloseAsync(window);
    }

    private static async Task CheckParentShutdownAsync(CodexQuotaCycle period)
    {
        using var runtime = new MonitorRuntime();
        var window = CreateWindow(typeof(QuotaCycleAnalysisWindow), period, null, null, runtime);
        var session = Get<AnalysisQuerySession>(window, "querySession");
        await runtime.SharedIoGate.WaitAsync();
        try
        {
            await ShowAsync(window);
            Require(LocalOperationCount(session) > 0, "child query admitted before parent shutdown");
            var stopped = await runtime.StopAsync(Budget);
            Require(stopped.Completed && stopped.PendingOperations.Count == 0, "parent waits for registered child to drain");
            await WaitUntilAsync(() => !Get<bool>(window, "analysisLoading"), "cancelled child UI load completes");
            Require(!Get<bool>(window, "hasSuccessfulResult"), "shutdown prevents late child publication");
            var called = false;
            try
            {
                await session.RunAsync("rejected after parent stop", _ => { called = true; return 1; });
                throw new InvalidOperationException("Probe failed: stopped parent accepted query");
            }
            catch (OperationCanceledException) { }
            Require(!called, "stopped parent rejects child delegate");
            await CloseAsync(window);
            Results.Add(new { check = "parent-shutdown", childDrained = true, latePublication = false, newQueryRejected = true });
        }
        finally
        {
            runtime.SharedIoGate.Release();
        }
    }

    private static (CodexQuotaEstimate Quota, IReadOnlyList<CodexQuotaCycle> Periods) SeedCaches()
    {
        var today = BeijingClock.Now;
        var start = new DateTimeOffset(today.Year, today.Month, today.Day, 0, 0, 0, CodexUsageReader.BeijingOffset).AddDays(-20);
        var usage = UsageCacheStore.Load();
        var quotaCache = QuotaSnapshotCacheStore.Load("CodexTokenMonitor");
        var periods = new List<CodexQuotaCycle>();
        for (var cycle = 0; cycle < 3; cycle++)
        {
            var cycleStart = start.AddDays(cycle * 7);
            var reset = cycleStart.AddDays(7);
            var events = Enumerable.Range(0, 9).Select(index => new TokenUsageEvent(
                cycleStart.AddHours(9 + index), 100_000 + index * 10_000, 20_000, 10_000, 0,
                110_000 + index * 10_000, $"round3:{cycle}:{index}", ModelId: "gpt-5.4")).ToArray();
            var bucket = new TokenUsageBucket { StartLocal = cycleStart };
            foreach (var item in events) bucket.Add(item);
            usage.Put(bucket, isComplete: true, scannedThroughLocal: cycleStart.AddDays(1).AddTicks(-1), detailEvents: events);
            var snapshots = events.Select((item, index) => new CodexQuotaSnapshot(
                item.Timestamp, "codex", "Codex", 10m + index, item.Timestamp.AddHours(5), index * 5m, reset)).ToArray();
            quotaCache.Put(DateOnly.FromDateTime(cycleStart.DateTime), snapshots, true, cycleStart.AddDays(1).AddTicks(-1));
            periods.Add(new(cycleStart, cycle == 2 ? today : reset, reset, snapshots.Length, 40m, cycle == 2));
        }
        usage.Save();
        var current = periods[^1];
        var summary = UsageCacheStore.Load().ReadRange(current.PeriodStart, today);
        var week = new CodexQuotaWindowEstimate("7d", 45m, 7 * 24 * 60, current.PeriodStart, today,
            current.ResetAt, summary, 1m, 2m, 2_000_000);
        return (new CodexQuotaEstimate(today, "codex", "Codex", null, week), periods);
    }

    private static long TimelineCount()
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = FixtureDatabasePath(), Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM quota_7d_timeline";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static string FixtureDatabasePath()
    {
        var path = Path.GetFullPath(UsageCacheStore.GetCachePath("CodexTokenMonitor"));
        Require(path.StartsWith(Path.GetFullPath(isolatedRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "all database mutations remain isolated");
        return path;
    }

    private static void Checkpoint(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        command.ExecuteNonQuery();
    }

    private static Window CreateWindow(Type type, params object?[] arguments)
    {
        var window = (Window)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            binder: null, args: arguments, culture: null)!;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = window.Top = -20000;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        RegisterWindow(window);
        return window;
    }

    private static void RegisterWindow(Window window)
    {
        if (Windows.Contains(window)) return;
        Windows.Add(window);
        window.Closed += (_, _) => ClosedWindows.Add(window);
    }

    private static async Task ShowAsync(Window window)
    {
        window.Show();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Require(window.IsLoaded, "real WPF Loaded lifecycle entered");
    }

    private static async Task CloseAsync(Window window)
    {
        if (!ClosedWindows.Contains(window)) window.Close();
        await WaitUntilAsync(() => ClosedWindows.Contains(window), "real WPF Closed event");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string description)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed > Budget) throw new TimeoutException("Probe timed out: " + description);
            await Task.Delay(10);
        }
    }

    private static async Task RenderAsync(Window window, string filename)
    {
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        var width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight));
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        image.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        var path = Path.Combine(outputRoot, filename);
        using var file = File.Create(path);
        encoder.Save(file);
        Results.Add(new { check = "render", path, width, height });
    }

    private static int LocalOperationCount(AnalysisQuerySession session) =>
        ((IDictionary)GetObject(Get<MonitorRuntime>(session, "localRuntime"), "operations")!).Count;

    private static T Get<T>(object target, string name) => (T)GetObject(target, name)!;
    private static object? GetObject(object target, string name)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        return target.GetType().GetField(name, flags) is { } field
            ? field.GetValue(target)
            : target.GetType().GetProperty(name, flags)!.GetValue(target);
    }
    private static object? Invoke(object target, string name, params object?[] arguments) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, arguments);
    private static Task InvokeTask(object target, string name) => (Task)Invoke(target, name)!;
    private static void Require(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException("Probe failed: " + description);
    }
    private static void WriteReport(bool passed, string? error) =>
        ProbeReport.Write(outputRoot, "analysis", passed, error, Results);
}
