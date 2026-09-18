using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexTokenMonitor;
using Microsoft.Data.Sqlite;

namespace CodexTokenMonitor.Wpf.Probes;

internal static class MainWindowProbe
{
    private static readonly DateTimeOffset SeedDay = new(2000, 1, 18, 0, 0, 0, TimeSpan.FromHours(8));
    private static readonly List<object> Results = new();
    private static readonly List<MainWindow> Windows = new();
    private static readonly HashSet<MainWindow> LoadedWindows = new();
    private static readonly Dictionary<MainWindow, TaskCompletionSource> ClosedSignals = new();
    private static readonly Dictionary<MainWindow, UiEventSuppressor.Scope> Suppressions = new();
    private static string outputRoot = "";
    private static string isolatedRoot = "";

    internal static void Run(string output)
    {
        outputRoot = output;
        isolatedRoot = Path.Combine(outputRoot, "isolated-" + Guid.NewGuid().ToString("N"));
        var logRoot = Path.Combine(isolatedRoot, "logs");
        // Include bootstrap, dispatcher work and failure cleanup in the same
        // path scopes. Closing a failed fixture must never restore real paths.
        using var cacheScope = MonitorCachePaths.PushLocalAppDataRoot(isolatedRoot);
        using var logScope = UsageLogPaths.PushRoot(logRoot);
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
            foreach (var window in Windows.AsEnumerable().Reverse())
            {
                try { await CloseWindowAsync(window); }
                catch (Exception ex) { failure = failure is null ? ex : new AggregateException(failure, ex); }
            }
            Environment.ExitCode = failure is null ? 0 : 1;
            try { WriteReport(failure is null, failure?.ToString()); }
            finally { app.Shutdown(Environment.ExitCode); }
        }));
        app.Run();
    }

    private static async Task RunAsync()
    {
        var logRoot = Path.Combine(isolatedRoot, "logs");
        Require(Path.GetFullPath(MonitorCachePaths.LocalAppData) == isolatedRoot, "cache isolation");
        new CodexDataSharingSettings { AutoStart = false }.Save();
        SeedCaches();
        var appServerType = typeof(UsageSource).Assembly.GetType("CodexTokenMonitor.CodexAppServerQuotaReader")!;
        var appServerAttempt = appServerType.GetField("lastAttemptUtc", BindingFlags.Static | BindingFlags.NonPublic)!;
        var attemptBefore = appServerAttempt.GetValue(null);
        var window = CreateWindow();
        var runtime = Get<MonitorRuntime>(window, "runtime");
        var mainWasLoaded = false;
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Loaded += (_, _) => mainWasLoaded = true;
        window.Closed += (_, _) => closed.TrySetResult();
        Get<DispatcherTimer>(window, "refreshTimer").Stop();
        SetSuppressed(window, true);
        Get<CheckBox>(window, "AutoRefreshBox").IsChecked = false;
        var modules = Get<IReadOnlyDictionary<UsageSource, UsageSourceModule>>(window, "usageModules");
        try
        {
            await DrainBindingsAsync(window);
            CheckDisplayBindings(window);
            Require(window.DataContext?.GetType().Name == "UsageDisplayViewModel", "main DataContext owns display state");
            Require(typeof(UsageSourceModule).Assembly == typeof(MainWindow).Assembly &&
                    modules.Values.All(module => module.GetType().Assembly == typeof(MainWindow).Assembly),
                "per-window module state belongs to WPF");
            Require(typeof(SelectedRange).Assembly == typeof(UsageSource).Assembly &&
                    typeof(UsageQueryResult).Assembly == typeof(UsageSource).Assembly,
                "shared range and query result models remain in Core");
            Results.Add(new { check = "display-bindings-initialized", boundProperties = DisplayBindingCount,
                dataContext = "UsageDisplayViewModel", modulesAssembly = typeof(UsageSourceModule).Assembly.GetName().Name,
                queryModelsAssembly = typeof(UsageQueryResult).Assembly.GetName().Name });
            var tabs = Get<TabControl>(window, "SourceTabs");
            Require(tabs.Items.Cast<TabItem>().Select(item => item.Header.ToString()).SequenceEqual(
                UsageSourceRegistry.All.Select(item => item.Title)), "registered tab headers preserve order");
            var claudeTab = tabs.Items.Cast<TabItem>().Single(item => Equals(item.Tag, UsageSource.ClaudeCode));
            tabs.Items.Remove(claudeTab);
            tabs.Items.Insert(0, claudeTab);
            tabs.SelectedItem = claudeTab;
            Require((UsageSource)Invoke(window, "CurrentSource")! == UsageSource.ClaudeCode, "source follows tag instead of hardcoded index");
            tabs.Items.Remove(claudeTab);
            tabs.Items.Insert(1, claudeTab);
            tabs.SelectedIndex = -1;
            Require((UsageSource)Invoke(window, "CurrentSource")! == UsageSource.Codex, "empty selection keeps default source");
            tabs.SelectedIndex = 0;
            Results.Add(new { check = "registered-tabs", originalOrder = true, tagBasedSelection = true, fallback = "Codex" });
            foreach (var source in Enum.GetValues<UsageSource>())
            {
                var multiplier = (int)source + 1;
                await CheckRefresh(window, modules[source], RangeMode.Day, 330 * multiplier, 2);
                await CheckRefresh(window, modules[source], RangeMode.Week, 880 * multiplier, 3);
                await CheckRefresh(window, modules[source], RangeMode.Month, 880 * multiplier, 3);
                await CheckRefresh(window, modules[source], RangeMode.Day, 770 * multiplier, 2, custom: true);
            }

            var codex = (CodexUsageModule)modules[UsageSource.Codex];
            await CheckRefresh(window, codex, RangeMode.Cycle, 770, 2);

            // A gate-held historical cycle must remain pending rather than
            // treating a cold display as safe for ungated query execution.
            codex.ClearDisplay();
            Select(window, codex, RangeMode.Cycle);
            var gate = Get<SemaphoreSlim>(window, "usageQueryGate");
            await gate.WaitAsync();
            var cycleBlocked = Refresh(window);
            var dispatcherAnswered = false;
            await window.Dispatcher.InvokeAsync(() => dispatcherAnswered = true, DispatcherPriority.Background);
            Require(dispatcherAnswered && !cycleBlocked.IsCompleted && codex.LastResult is null,
                "historical cycle gate blocks I/O while dispatcher responds");
            gate.Release();
            await cycleBlocked.WaitAsync(TimeSpan.FromSeconds(20));
            Require(codex.LastResult?.Summary.TotalTokens == 770, "historical cycle resumes");
            Results.Add(new { check = "historical-cycle-gate", dispatcherResponsive = true, tokens = 770 });

            // Only an already materialized display may bypass the shared gate.
            Select(window, codex, RangeMode.Cycle);
            await gate.WaitAsync();
            var cachedCycle = Refresh(window);
            Require(cachedCycle.IsCompletedSuccessfully, "materialized cycle display bypasses gate");
            gate.Release();
            Results.Add(new { check = "materialized-cycle-display", bypassesGate = true });

            await CheckSourceSelectionRoundTripAsync(window, modules);

            foreach (var module in modules.Values) module.ClearDisplay();
            Select(window, codex, RangeMode.Day);
            await gate.WaitAsync();
            var burst = Refresh(window);
            Select(window, codex, RangeMode.Month);
            Require(ReferenceEquals(burst, Refresh(window)), "burst shares run completion");
            var claude = modules[UsageSource.ClaudeCode];
            Select(window, claude, RangeMode.Week);
            Require(ReferenceEquals(burst, Refresh(window)), "latest source joins same run");
            Require(!burst.IsCompleted && codex.LastResult is null, "stale first query remains unpublished");
            gate.Release();
            await burst.WaitAsync(TimeSpan.FromSeconds(20));
            await DrainBindingsAsync(window);
            CheckDisplayBindings(window);
            Require(codex.LastResult is null, "superseded source is never published");
            Require(claude.LastRange?.Mode == RangeMode.Week && claude.LastResult?.Summary.TotalTokens == 1760,
                "latest source and range win");
            Require(window.Title.StartsWith("Claude Code", StringComparison.Ordinal), "latest source title");
            Results.Add(new { check = "latest-wins-source-and-range", supersededPublished = false, tokens = 1760 });

            await CheckDisplayStateTransitionsAsync(window, codex);
            await CheckRealSourceSwitchBindingsAsync(window, codex, claude);

            await CheckSettingsAsync(window, codex);
            await CheckReserveUsageDisplayAsync(window, codex, modules[UsageSource.ClaudeCode]);
            await CheckCacheFailureAndRecoveryAsync(window, codex, "CodexTokenMonitor", isolatedRoot, preserveDisplay: true);
            await CheckIndependentWindowRecoveryAsync(window, isolatedRoot);

            Select(window, codex, RangeMode.Day);
            await RefreshAndDrainAsync(window);
            await RenderContentAsync(window, Path.Combine(outputRoot, "wpf-architecture-probe.png"));
            Require(!mainWasLoaded, "main Loaded lifecycle never entered");

            // Closing must keep the dispatcher alive while registered work
            // drains, and must cancel a real gate-waiting production refresh.
            var titleBeforeClose = window.Title;
            var valueBeforeClose = Get<TextBlock>(window, "TotalValue").Text;
            codex.ClearDisplay();
            Select(window, codex, RangeMode.Month);
            await gate.WaitAsync();
            var closingRefresh = Refresh(window);
            var releaseWork = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var delayedWork = runtime.Run("probe controlled drain", _ => releaseWork.Task);
            window.Close();
            Require(!closed.Task.IsCompleted && Get<bool>(window, "isClosed") && runtime.IsStopping,
                "first Closing is deferred and seals new runtime work");
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            Require(runtime.IsStopping && !closed.Task.IsCompleted, "runtime seals admission while Closing waits");
            await DrainBindingsAsync(window);
            Require(!Get<Button>(window, "CopySummaryButton").IsEnabled, "stopped view model disables bound copy command");
            CheckDisplayBindings(window);
            gate.Release();
            try { await closingRefresh.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (OperationCanceledException) { }
            Require(window.Title == titleBeforeClose && Get<TextBlock>(window, "TotalValue").Text == valueBeforeClose,
                "closed window does not receive query results");
            Require(codex.LastResult is null && gate.CurrentCount == 1, "cancelled query releases gate without publishing");
            var closingDispatcherResponded = false;
            await window.Dispatcher.InvokeAsync(() => closingDispatcherResponded = true, DispatcherPriority.Background);
            Require(closingDispatcherResponded && !closed.Task.IsCompleted, "dispatcher responds during pending shutdown");
            releaseWork.TrySetResult();
            await delayedWork.WaitAsync(TimeSpan.FromSeconds(10));
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (GetObject(window, "shutdownTask") is Task shutdownTask)
                await shutdownTask.WaitAsync(TimeSpan.FromSeconds(10));
            Require(Get<bool>(window, "shutdownComplete"), "second Closing completes");
            Results.Add(new { check = "real-closing-drain", firstCloseDeferred = true, dispatcherResponsive = true,
                resultPublished = false, gateReleasedBeforeDisposal = true, finalClosed = true });

            Require(Equals(attemptBefore, appServerAttempt.GetValue(null)), "no live app-server quota reads");
            Require(GetObject(window, "dataSharingServer") is null, "no sharing server started");
            Require(!Directory.EnumerateFiles(logRoot, "*", SearchOption.AllDirectories).Any(), "no source log fixtures or writes");
            Require(LoadedWindows.Count == 0, "no main window entered Loaded");
            Require(ClosedSignals.Values.All(signal => signal.Task.IsCompletedSuccessfully), "all created main windows reached Closed");
            Results.Add(new { check = "isolation", isolatedRoot, mainLoaded = LoadedWindows.Count > 0, appServerReads = 0,
                sharingServerStarted = false, windowsCreated = Windows.Count,
                closedWindows = ClosedSignals.Values.Count(signal => signal.Task.IsCompletedSuccessfully), allClosed = true });
        }
        finally
        {
            if (!closed.Task.IsCompleted)
            {
                window.Close();
                await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }

    private static async Task CheckSourceSelectionRoundTripAsync(MainWindow window,
        IReadOnlyDictionary<UsageSource, UsageSourceModule> modules)
    {
        // A cold Codex display keeps this test focused on saved selection state.
        // Its queued request is superseded before release, so neither quota
        // refresh nor the main Loaded/network lifecycle is entered.
        var codex = (CodexUsageModule)modules[UsageSource.Codex];
        codex.ClearDisplay();
        var chosenCycle = new CodexQuotaCycle(SeedDay.AddHours(9.5), SeedDay.AddDays(2), SeedDay.AddDays(2), 0, null, false);
        codex.QuotaCycles = new[]
        {
            new CodexQuotaCycle(SeedDay.AddDays(2), SeedDay.AddDays(9), SeedDay.AddDays(9), 0, null, false),
            chosenCycle
        };
        codex.SelectedCycle = chosenCycle;
        var expected = new Dictionary<UsageSource, ModuleSelection>
        {
            [UsageSource.Codex] = new(RangeMode.Cycle, SeedDay.DateTime, null, chosenCycle),
            [UsageSource.ClaudeCode] = new(RangeMode.Week, SeedDay.AddDays(2).AddHours(15.5).DateTime, null, null),
            [UsageSource.ZCode] = new(RangeMode.Month, SeedDay.AddDays(3).DateTime, null, null),
            [UsageSource.WorkBuddy] = new(RangeMode.Day, SeedDay.DateTime, SeedDay.AddHours(9.5), null),
            [UsageSource.Dsh] = new(RangeMode.Day, SeedDay.AddDays(1).DateTime, null, null)
        };
        foreach (var (source, selection) in expected)
        {
            modules[source].Mode = selection.Mode;
            modules[source].PickerValue = selection.Picker;
            modules[source].CustomStartLocal = selection.CustomStart;
        }
        Set(window, "activeSource", UsageSource.Dsh);
        var tabs = Get<TabControl>(window, "SourceTabs");
        tabs.SelectedItem = tabs.Items.Cast<TabItem>().Single(tab => Equals(tab.Tag, UsageSource.Dsh));
        Invoke(window, "UpdateRangeControls");
        var gate = Get<SemaphoreSlim>(window, "usageQueryGate");
        await gate.WaitAsync();
        Task completion;
        try
        {
            SetSuppressed(window, false);
            var visits = new[] { UsageSource.Codex, UsageSource.ClaudeCode, UsageSource.ZCode, UsageSource.WorkBuddy,
                UsageSource.Dsh, UsageSource.WorkBuddy, UsageSource.ZCode, UsageSource.ClaudeCode, UsageSource.Codex, UsageSource.Dsh };
            foreach (var source in visits)
            {
                tabs.SelectedItem = tabs.Items.Cast<TabItem>().Single(tab => Equals(tab.Tag, source));
                Require(Get<UsageSource>(window, "activeSource") == source, "actual tab handler selects the requested module");
                Require(Get<ComboBox>(window, "RangeModeBox").SelectedIndex == (int)expected[source].Mode,
                    "returning to a source restores its own range mode control");
                Require(Get<DatePicker>(window, "DatePicker").SelectedDate == expected[source].Picker.Date &&
                        Equals(GetObject(GetObject(window, "WeekEndPicker")!, "Value"), expected[source].Picker),
                    "returning to a source restores its own date and week endpoint controls");
                Require(SelectionOf(modules[source]) == expected[source], "returning to a source preserves its custom start and cycle");
                if (source == UsageSource.Codex)
                    Require(ReferenceEquals(Get<ComboBox>(window, "CycleBox").SelectedItem, chosenCycle) &&
                            Get<ComboBox>(window, "CycleBox").SelectedIndex == 1,
                        "returning to Codex preserves the nondefault historical cycle selection");
            }
            completion = Get<Task>(GetObject(window, "usageRefreshRunner")!, "Completion");
            Require(!completion.IsCompleted, "source round trip never waits synchronously for the I/O gate");
            await DrainBindingsAsync(window);
            CheckDisplayBindings(window);
        }
        finally
        {
            SetSuppressed(window, true);
            gate.Release();
        }
        await completion.WaitAsync(TimeSpan.FromSeconds(20));
        await DrainBindingsAsync(window);
        foreach (var (source, selection) in expected)
            Require(SelectionOf(modules[source]) == selection, "all five source selections remain independent after the refresh drains");
        Require(codex.LastResult is null && modules[UsageSource.Dsh].LastResult?.Summary.TotalTokens == 2750,
            "only the final source query publishes after the round trip");
        Require(DisplayStage(window) == "Ready" && Get<Button>(window, "CopySummaryButton").IsEnabled,
            "final source display is ready and copyable");
        CheckDisplayBindings(window);
        await RenderContentAsync(window, Path.Combine(outputRoot, "main-source-round-trip.png"));
        Results.Add(new { check = "source-selection-round-trip", sources = expected.Count, actualTabChanges = 10,
            customStartPreserved = true, historicalCycleIndex = 1, independentRanges = true, finalSource = "Dsh", tokens = 2750 });
    }

    private static async Task CheckIndependentWindowRecoveryAsync(MainWindow primary, string isolatedRoot)
    {
        var primaryModules = Get<IReadOnlyDictionary<UsageSource, UsageSourceModule>>(primary, "usageModules");
        var primarySelections = primaryModules.ToDictionary(pair => pair.Key, pair => SelectionOf(pair.Value));
        var primaryResults = primaryModules.ToDictionary(pair => pair.Key, pair => pair.Value.LastResult);
        var primarySnapshot = GetObject(primary.DataContext!, "Snapshot");
        var primaryRows = Get<DataGrid>(primary, "BreakdownGrid").ItemsSource;
        var primaryStatus = Get<TextBlock>(primary, "StatusText").Text;
        var secondary = CreateWindow();
        var secondaryClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondaryLoaded = false;
        secondary.Loaded += (_, _) => secondaryLoaded = true;
        secondary.Closed += (_, _) => secondaryClosed.TrySetResult();
        Get<DispatcherTimer>(secondary, "refreshTimer").Stop();
        SetSuppressed(secondary, true);
        Get<CheckBox>(secondary, "AutoRefreshBox").IsChecked = false;
        var secondaryModules = Get<IReadOnlyDictionary<UsageSource, UsageSourceModule>>(secondary, "usageModules");
        try
        {
            Require(!ReferenceEquals(primary.DataContext, secondary.DataContext) &&
                    !ReferenceEquals(GetObject(primary, "runtime"), GetObject(secondary, "runtime")) &&
                    !ReferenceEquals(GetObject(primary, "usageQueryGate"), GetObject(secondary, "usageQueryGate")),
                "each window owns its own display state and runtime");
            foreach (var source in Enum.GetValues<UsageSource>())
                Require(!ReferenceEquals(primaryModules[source], secondaryModules[source]), "module instances are never shared between windows");
            Select(secondary, secondaryModules[UsageSource.ClaudeCode], RangeMode.Week);
            await RefreshAndDrainAsync(secondary);
            Require(secondaryModules[UsageSource.ClaudeCode].LastResult?.Summary.TotalTokens == 1760,
                "secondary window loads an independent source and range");
            await RenderContentAsync(secondary, Path.Combine(outputRoot, "main-independent-window.png"));

            // Reuse the existing real corruption/recovery scenario in a second
            // window; it must not mutate the first window's successful display.
            await CheckCacheFailureAndRecoveryAsync(secondary, secondaryModules[UsageSource.ClaudeCode],
                "ClaudeCodeTokenMonitor", isolatedRoot, preserveDisplay: false);
            Require(ReferenceEquals(primarySnapshot, GetObject(primary.DataContext!, "Snapshot")) &&
                    ReferenceEquals(primaryRows, Get<DataGrid>(primary, "BreakdownGrid").ItemsSource) &&
                    Get<TextBlock>(primary, "StatusText").Text == primaryStatus,
                "secondary failure and recovery do not replace the primary snapshot, rows or status");
            foreach (var source in Enum.GetValues<UsageSource>())
                Require(SelectionOf(primaryModules[source]) == primarySelections[source] &&
                        ReferenceEquals(primaryModules[source].LastResult, primaryResults[source]),
                    "secondary navigation and recovery do not mutate primary module state");
            Require(!secondaryLoaded && GetObject(secondary, "dataSharingServer") is null,
                "secondary window also avoids main Loaded and sharing lifecycle");
            CheckDisplayBindings(primary);
        }
        finally
        {
            secondary.Close();
            await secondaryClosed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (GetObject(secondary, "shutdownTask") is Task shutdownTask)
                await shutdownTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Require(!Get<MonitorRuntime>(primary, "runtime").IsStopping, "secondary Close does not stop the primary runtime");
        await RefreshAndDrainAsync(primary);
        Require(primaryModules[UsageSource.Codex].LastResult?.Summary.TotalTokens == 330 &&
                Get<Button>(primary, "CopySummaryButton").IsEnabled, "primary remains usable after secondary closes");
        Results.Add(new { check = "independent-window-recovery", sourceModules = primaryModules.Count,
            separateRuntime = true, primarySelectionAndDisplayPreserved = true, secondaryClosed = true, primaryStillUsable = true });
    }

    private sealed record ModuleSelection(RangeMode Mode, DateTime Picker, DateTimeOffset? CustomStart, CodexQuotaCycle? Cycle);
    private static ModuleSelection SelectionOf(UsageSourceModule module) =>
        new(module.Mode, module.PickerValue, module.CustomStartLocal, (module as CodexUsageModule)?.SelectedCycle);

    private static async Task CheckSettingsAsync(MainWindow window, CodexUsageModule codex)
    {
        await SettingsRefresh(window);
        var now = BeijingClock.Now;
        var plan = new SubscriptionPlanRecord { Id = "main-probe-plan", PlanName = "Probe Pro", StartLocal = now.AddDays(-1), EndLocal = now.AddDays(20), AmountCny = 1234m };
        SubscriptionPlanStore.Save(new[] { plan });
        ResetOpportunityStore.Save(new[] { new ResetOpportunityRecord { GrantedLocal = now.AddDays(-1), ExpiresLocal = now.AddDays(20), Note = "main probe" } });
        await SettingsRefresh(window);
        Require(Get<TextBlock>(window, "PlanSpendValue").Text == "Probe Pro", "healthy plan snapshot displayed");
        Require(Get<TextBlock>(window, "ResetOpportunityDetail").Text == "1 张可用", "healthy reset snapshot displayed");
        Results.Add(new { check = "main-settings-healthy", plan = "Probe Pro", resetCount = 1 });

        // Damage only the plan table, preserving the independently read reset card.
        ChangePlanEnd("invalid-date");
        await SettingsRefresh(window);
        Require(Get<TextBlock>(window, "PlanSpendValue").Text == "Probe Pro", "failed plan read preserves successful summary");
        Require(Get<TextBlock>(window, "PlanSpendDetail").Text.Contains("沿用上次设置"), "retained plan snapshot visibly marked");
        Require(Get<TextBlock>(window, "ResetOpportunityDetail").Text == "1 张可用", "plan failure does not affect reset summary");
        Select(window, codex, RangeMode.Day);
        await RefreshAndDrainAsync(window);
        Require(codex.LastResult?.Summary.TotalTokens == 330, "plan failure does not block tokens");
        await SettingsRefresh(window);
        Results.Add(new { check = "main-settings-failure-isolation", preservedPlan = true, resetsHealthy = true, tokens = 330 });

        var firstWindow = CreateWindow();
        try
        {
            await SettingsRefresh(firstWindow);
            Require(Get<TextBlock>(firstWindow, "PlanSpendValue").Text == "暂不可用", "first plan failure is unavailable");
            Require(Get<TextBlock>(firstWindow, "ResetOpportunityDetail").Text == "1 张可用", "first window independent reset successful");
        }
        finally { await CloseWindowAsync(firstWindow); }
        Results.Add(new { check = "main-settings-first-failure", unavailable = true });

        ChangePlanEnd(plan.EndLocal.ToString("O"));
        await SettingsRefresh(window);
        Require(!Get<TextBlock>(window, "PlanSpendDetail").Text.Contains("沿用"), "plan recovery clears warning");
        Results.Add(new { check = "main-settings-recovery", plan = "Probe Pro" });

        var pricePath = Path.Combine(MonitorCachePaths.LocalAppData, "CodexTokenMonitor", "price-settings.json");
        PriceSettingsStore.Save(PriceSettingsStore.Current.Clone());
        var originalPrices = File.ReadAllText(pricePath);
        var displayed = codex.LastResult;
        File.WriteAllText(pricePath, "broken prices");
        await RefreshAndDrainAsync(window);
        Require(ReferenceEquals(displayed, codex.LastResult), "price failure retains successful display");
        Require(Get<TextBlock>(window, "StatusText").Text.Contains("失败"), "price failure visible");
        Require(File.ReadAllText(pricePath) == "broken prices", "automatic model prices never overwrite bad configuration");
        Results.Add(new { check = "main-price-failure", displayPreserved = true, filePreserved = true });
        File.WriteAllText(pricePath, originalPrices);
        await RefreshAndDrainAsync(window);
        Require(codex.LastResult?.Summary.TotalTokens == 330 && !Get<TextBlock>(window, "StatusText").Text.Contains("失败"), "price recovery resumes query");
        Results.Add(new { check = "main-price-recovery", tokens = 330 });

        static Task SettingsRefresh(MainWindow target) => ((Task)Invoke(target, "RefreshSettingsSummaryAsync", true)!).WaitAsync(TimeSpan.FromSeconds(20));
        static void ChangePlanEnd(string end)
        {
            using var connection = MonitorSettingsDatabase.OpenConnection(MonitorSettingsDatabase.Path);
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE subscription_plans SET end_local = $end WHERE id = 'main-probe-plan'";
            command.Parameters.AddWithValue("$end", end);
            command.ExecuteNonQuery();
        }
    }

    private static async Task CheckCacheFailureAndRecoveryAsync(MainWindow window, UsageSourceModule module,
        string folder, string isolatedRoot, bool preserveDisplay)
    {
        Select(window, module, RangeMode.Day);
        await RefreshAndDrainAsync(window);
        var successful = module.LastResult;
        var expectedTokens = successful!.Summary.TotalTokens;
        var successfulText = Get<TextBlock>(window, "TotalValue").Text;
        var successfulRows = Get<DataGrid>(window, "BreakdownGrid").ItemsSource;
        var successfulSnapshot = GetObject(window.DataContext!, "Snapshot");
        var store = UsageCacheStore.Load(folder);
        var path = Path.GetFullPath(UsageCacheStore.GetCachePath(folder));
        Require(path.StartsWith(Path.GetFullPath(isolatedRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "only isolated fixture databases may be replaced");
        var backup = path + ".healthy-backup";

        // Checkpoint before copying so the backup includes all seeded rows,
        // then release pooled handles before changing the fixture file.
        using (var database = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false
        }.ToString()))
        {
            database.Open();
            using var checkpoint = database.CreateCommand();
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            checkpoint.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        File.Copy(path, backup, overwrite: true);
        if (!preserveDisplay) module.ClearDisplay();
        try
        {
            File.WriteAllText(path, "This isolated test fixture is deliberately not a SQLite database.");
            await RefreshAndDrainAsync(window);
            var status = Get<TextBlock>(window, "StatusText").Text;
            Require(status.Contains("失败", StringComparison.Ordinal) || status.Contains("不可用", StringComparison.Ordinal),
                "cache failure is visible in status");
            Require(Get<TextBlock>(window, "StatusText").ToolTip is string { Length: > 0 }, "cache error detail reaches bound tooltip");
            if (preserveDisplay)
            {
                Require(ReferenceEquals(successful, module.LastResult), "failed cache read preserves successful display");
                Require(module.LastResult!.Summary.TotalTokens == expectedTokens, "failure does not become zero usage");
                Require(ReferenceEquals(successfulSnapshot, GetObject(window.DataContext!, "Snapshot")) &&
                        Get<TextBlock>(window, "TotalValue").Text == successfulText &&
                        ReferenceEquals(successfulRows, Get<DataGrid>(window, "BreakdownGrid").ItemsSource), "stale display retains immutable snapshot, visible metrics and rows");
                Require(DisplayStage(window) == "Stale", "failed refresh marks retained data stale");
                Require(Get<FrameworkElement>(window, "UsageSummaryPanel").Visibility == Visibility.Visible &&
                        Get<FrameworkElement>(window, "EmptyUsagePanel").Visibility == Visibility.Collapsed,
                    "retained result stays in content view");
                Require(Get<Button>(window, "CopySummaryButton").IsEnabled, "retained result remains copyable once refresh ends");
            }
            else
            {
                Require(module.LastResult is null, "first failed read does not publish an empty result");
                var emptyTitle = Get<TextBlock>(window, "EmptyUsageTitle").Text;
                Require(emptyTitle.Contains("失败", StringComparison.Ordinal) || emptyTitle.Contains("不可用", StringComparison.Ordinal),
                    "no successful history is shown as a read failure");
                Require(!emptyTitle.Contains("无用量", StringComparison.Ordinal), "cache error is not labelled no usage");
                Require(Get<FrameworkElement>(window, "UsageSummaryPanel").Visibility == Visibility.Collapsed &&
                        Get<FrameworkElement>(window, "EmptyUsagePanel").Visibility == Visibility.Visible,
                    "first failure uses unavailable view");
                Require(!Get<Button>(window, "CopySummaryButton").IsEnabled, "first failure cannot copy prior source values");
                Require(DisplayStage(window) == "Unavailable", "first failure stage is unavailable");
            }
            CheckDisplayBindings(window);
            await RenderContentAsync(window, Path.Combine(outputRoot, preserveDisplay ? "main-stale-result.png" : "main-first-failure.png"));
            Invoke(window, "SetStatus", "绑定状态恢复消息");
            await DrainBindingsAsync(window);
            Require(Get<TextBlock>(window, "StatusText").Text == "绑定状态恢复消息" && Get<TextBlock>(window, "StatusText").ToolTip is null,
                "ordinary status atomically replaces old failure detail");
            CheckDisplayBindings(window);
            Results.Add(new { check = "cache-failure-ui", source = module.Source.ToString(), preserveDisplay,
                status, previousTokens = expectedTokens });
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Copy(backup, path, overwrite: true);
        }
        await RefreshAndDrainAsync(window);
        Require(ReferenceEquals(store, UsageCacheStore.Load(folder)), "recovery reuses the existing cache store");
        Require(module.LastResult?.Summary.TotalTokens == expectedTokens, "new operation recovers after database restoration");
        var recoveredStatus = Get<TextBlock>(window, "StatusText").Text;
        Require(!recoveredStatus.Contains("失败", StringComparison.Ordinal) && !recoveredStatus.Contains("不可用", StringComparison.Ordinal),
            "recovery removes stale cache failure status");
        Require(DisplayStage(window) == "Ready" && Get<TextBlock>(window, "StatusText").ToolTip is null,
            "recovered view returns to ready and clears failure detail");
        Results.Add(new { check = "cache-recovery", source = module.Source.ToString(), tokens = expectedTokens, sameStore = true });
    }

    private static async Task CheckReserveUsageDisplayAsync(MainWindow window, CodexUsageModule codex, UsageSourceModule nonCodex)
    {
        Set(window, "activeSource", UsageSource.Codex);
        Invoke(window, "ApplyQuotaSummary", codex, BuildQuotaWithReserve(false));
        Require(Get<FrameworkElement>(window, "ReserveUsagePanel").Visibility == Visibility.Collapsed,
            "reserve usage panel stays hidden without reserve model usage");

        Invoke(window, "ApplyQuotaSummary", codex, BuildQuotaWithReserve(true));
        Require(Get<FrameworkElement>(window, "ReserveUsagePanel").Visibility == Visibility.Visible,
            "reserve usage panel appears after reserve model usage");
        Require(Get<TextBlock>(window, "ReserveUsageValue").Text.Contains("M", StringComparison.Ordinal) &&
                Get<TextBlock>(window, "ReserveUsageDetail").Text.Contains("按 Luna", StringComparison.Ordinal),
            "reserve usage panel shows tokens and Luna pricing");
        await RenderContentAsync(window, Path.Combine(outputRoot, "reserve-usage-ui.png"));

        Invoke(window, "ApplyQuotaSummary", nonCodex, null);
        Require(Get<FrameworkElement>(window, "ReserveUsagePanel").Visibility == Visibility.Collapsed,
            "reserve usage panel clears when switching away from Codex");
        Results.Add(new { check = "reserve-usage-ui", hiddenWithoutUsage = true, shownAfterUsage = true,
            hiddenForNonCodex = true, value = "1.05M", pricing = "GPT-5.6 Luna" });
    }

    private static CodexQuotaEstimate BuildQuotaWithReserve(bool includeReserve)
    {
        var now = DateTimeOffset.UtcNow.ToOffset(CodexUsageReader.BeijingOffset);
        var usage = new TokenUsageSummary { StartLocal = now.AddMinutes(-30), EndLocal = now };
        if (includeReserve)
        {
            usage.Add(new TokenUsageEvent(now.AddMinutes(-1), 1_000_000, 600_000, 50_000, 10_000,
                1_050_000, "reserve-probe", 100_000, CodexModelCost.ReserveModelId));
        }

        var fiveHour = new CodexQuotaWindowEstimate("5h", 20m, 5 * 60, usage.StartLocal,
            usage.EndLocal, now.AddHours(4), usage, 1m, null, null);
        return new CodexQuotaEstimate(now, "codex", "Codex", fiveHour, null);
    }

    private static async Task CheckRefresh(MainWindow window, UsageSourceModule module, RangeMode mode,
        long expectedTokens, long expectedEvents, bool custom = false)
    {
        Select(window, module, mode, custom);
        await RefreshAndDrainAsync(window);
        var result = module.LastResult;
        Require(result is not null && result.Summary.TotalTokens == expectedTokens && result.Summary.Events == expectedEvents,
            $"{module.Source}/{mode}/custom={custom} summary");
        Require(UsageBreakdownBuilder.CountEvents(result!.BreakdownRows) == expectedEvents, "breakdown agrees with summary");
        Require(Get<TextBlock>(window, "EventsValue").Text == expectedEvents.ToString("N0"), "visible event count agrees");
        Results.Add(new { check = "cache-only-refresh", source = module.Source.ToString(), mode = mode.ToString(), custom,
            tokens = result.Summary.TotalTokens, events = result.Summary.Events, rows = result.BreakdownRows.Count });
    }

    private static async Task CheckDisplayStateTransitionsAsync(MainWindow window, CodexUsageModule module)
    {
        Select(window, module, RangeMode.Day);
        await RefreshAndDrainAsync(window);
        Require(Get<FrameworkElement>(window, "UsageSummaryPanel").Visibility == Visibility.Visible, "ready result shows content");
        Require(DisplayStage(window) == "Ready", "successful query enters ready stage");
        Require(Get<Button>(window, "CopySummaryButton").IsEnabled, "ready result is copyable");
        var text = Get<TextBlock>(window, "TotalValue").Text;
        var rows = Get<DataGrid>(window, "BreakdownGrid").ItemsSource;
        Invoke(window, "SetBusy", true);
        await DrainBindingsAsync(window);
        Require(!Get<Button>(window, "CopySummaryButton").IsEnabled, "busy display disables copy");
        Require(Get<TextBlock>(window, "TotalValue").Text == text && ReferenceEquals(rows, Get<DataGrid>(window, "BreakdownGrid").ItemsSource),
            "busy change preserves displayed values and rows");
        CheckDisplayBindings(window);
        Invoke(window, "SetBusy", false);
        await DrainBindingsAsync(window);
        Require(Get<Button>(window, "CopySummaryButton").IsEnabled, "leaving busy restores copy");
        CheckDisplayBindings(window);
        Results.Add(new { check = "busy-copy-binding", contentsPreserved = true, disabledWhileBusy = true, reenabled = true });

        var snapshotBeforeImport = GetObject(window.DataContext!, "Snapshot");
        Invoke(window, "NotifySharedDataImported", new CodexDataImportResult(1, 1, 3, 0, 2, 0), false);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await DrainBindingsAsync(window);
        Require(module.LastResult is null, "shared import invalidates the materialized copy source");
        Require(ReferenceEquals(snapshotBeforeImport, GetObject(window.DataContext!, "Snapshot")) &&
                Get<TextBlock>(window, "TotalValue").Text == text &&
                ReferenceEquals(rows, Get<DataGrid>(window, "BreakdownGrid").ItemsSource),
            "shared import without refresh preserves the visible snapshot and rows");
        Require(!Get<Button>(window, "CopySummaryButton").IsEnabled, "shared import without refresh disables stale copy source");
        Require(Get<TextBlock>(window, "StatusText").Text.Contains("共享数据已合并"), "local shared import notification completes");
        CheckDisplayBindings(window);
        await RefreshAndDrainAsync(window);
        Require(module.LastResult?.Summary.TotalTokens == 330 && Get<Button>(window, "CopySummaryButton").IsEnabled,
            "ordinary refresh after shared import restores materialized data and copy");
        Results.Add(new { check = "shared-import-copy-binding", refreshRequested = false, snapshotPreserved = true,
            rowsPreserved = true, disabledAfterInvalidation = true, reenabledAfterRefresh = true });

        // Use a real cache-only query over a historical day with no seeded events.
        module.PickerValue = SeedDay.AddDays(10).DateTime;
        Invoke(window, "UpdateRangeControls");
        await RefreshAndDrainAsync(window);
        Require(module.LastResult is { } empty && empty.Summary.Events == 0 && empty.Summary.TotalTokens == 0, "empty query is successful data");
        Require(DisplayStage(window) == "Empty", "successful zero result enters empty stage");
        Require(Get<FrameworkElement>(window, "UsageSummaryPanel").Visibility == Visibility.Collapsed &&
                Get<FrameworkElement>(window, "UsageMetricsPanel").Visibility == Visibility.Collapsed &&
                Get<FrameworkElement>(window, "UsageDetailsPanel").Visibility == Visibility.Collapsed &&
                Get<FrameworkElement>(window, "EmptyUsagePanel").Visibility == Visibility.Visible, "successful empty result selects empty view");
        Require(Get<TextBlock>(window, "EmptyUsageTitle").Text.Contains("Codex") &&
                Get<TextBlock>(window, "EmptyUsageTitle").Text.Contains("暂无") &&
                !Get<TextBlock>(window, "EmptyUsageTitle").Text.Contains("失败"), "empty title describes source and no usage");
        Require(Get<TextBlock>(window, "EmptyUsagePeriod").Text.Contains("2000-01-28"), "empty period is the newly selected range");
        Require(!Get<Button>(window, "CopySummaryButton").IsEnabled && Get<TextBlock>(window, "StatusText").ToolTip is null,
            "successful empty result disables copy without warning");
        Require(Get<Panel>(window, "CostCardsPanel").Children.Count == 0 && !ReferenceEquals(rows, Get<DataGrid>(window, "BreakdownGrid").ItemsSource),
            "empty transition clears previous cards and detail rows");
        await RenderContentAsync(window, Path.Combine(outputRoot, "main-successful-empty.png"));
        Results.Add(new { check = "successful-empty-binding", source = "Codex", period = "2000-01-28", canCopy = false, warning = false });

        Select(window, module, RangeMode.Day);
        await RefreshAndDrainAsync(window);
        Require(Get<FrameworkElement>(window, "UsageSummaryPanel").Visibility == Visibility.Visible &&
                Get<FrameworkElement>(window, "EmptyUsagePanel").Visibility == Visibility.Collapsed &&
                Get<Button>(window, "CopySummaryButton").IsEnabled, "empty result recovers to visible data and copy");
        await RenderContentAsync(window, Path.Combine(outputRoot, "main-empty-recovered.png"));
        Results.Add(new { check = "empty-to-ready-binding", tokens = module.LastResult!.Summary.TotalTokens, canCopy = true });
    }

    private static async Task CheckRealSourceSwitchBindingsAsync(MainWindow window, CodexUsageModule codex, UsageSourceModule claude)
    {
        Select(window, codex, RangeMode.Day);
        await RefreshAndDrainAsync(window);
        claude.Mode = RangeMode.Week;
        claude.PickerValue = SeedDay.AddDays(2).DateTime;
        claude.CustomStartLocal = null;
        var tabs = Get<TabControl>(window, "SourceTabs");
        var gate = Get<SemaphoreSlim>(window, "usageQueryGate");
        await gate.WaitAsync();
        Task completion;
        try
        {
            SetSuppressed(window, false);
            tabs.SelectedItem = tabs.Items.Cast<TabItem>().Single(tab => Equals(tab.Tag, UsageSource.ClaudeCode));
            tabs.SelectedItem = tabs.Items.Cast<TabItem>().Single(tab => Equals(tab.Tag, UsageSource.Codex));
            tabs.SelectedItem = tabs.Items.Cast<TabItem>().Single(tab => Equals(tab.Tag, UsageSource.ClaudeCode));
            completion = Get<Task>(GetObject(window, "usageRefreshRunner")!, "Completion");
            Require(!completion.IsCompleted, "real selection handlers queue behind shared gate");
            await DrainBindingsAsync(window);
            Require(window.Title.StartsWith("Claude Code", StringComparison.Ordinal), "source selection immediately restores its own display");
            Require(!Get<Button>(window, "CopySummaryButton").IsEnabled, "source switch stays busy while its refresh is pending");
            CheckDisplayBindings(window);
        }
        finally
        {
            SetSuppressed(window, true);
            gate.Release();
        }
        await completion.WaitAsync(TimeSpan.FromSeconds(20));
        await DrainBindingsAsync(window);
        CheckDisplayBindings(window);
        Require(claude.LastRange?.Mode == RangeMode.Week && claude.LastResult?.Summary.TotalTokens == 1760, "latest real source selection wins");
        Require(window.Title.StartsWith("Claude Code", StringComparison.Ordinal) &&
                Get<TextBlock>(window, "EventsValue").Text == "3" &&
                Get<TextBlock>(window, "PeriodValue").Text.Contains("2000-01-20"), "source, range and metrics bindings agree");
        Require(Get<UsageSource>(GetObject(window.DataContext!, "Snapshot")!, "Source") == UsageSource.ClaudeCode &&
                DisplayStage(window) == "Ready", "accepted snapshot belongs to latest source and is ready");
        Require(Get<Button>(window, "CopySummaryButton").IsEnabled && Get<TextBlock>(window, "StatusText").ToolTip is null,
            "source switch finishes copyable and without old warning detail");
        Results.Add(new { check = "real-source-switch-binding", finalSource = "ClaudeCode", mode = "Week", tokens = 1760, coherentBindings = true });
    }

    private static void Select(MainWindow window, UsageSourceModule module, RangeMode mode, bool custom = false)
    {
        Set(window, "activeSource", module.Source);
        module.Mode = mode;
        module.CustomStartLocal = custom ? SeedDay.AddHours(9.5) : null;
        module.PickerValue = (mode == RangeMode.Week ? SeedDay.AddDays(2) : SeedDay).DateTime;
        Get<TabControl>(window, "SourceTabs").SelectedIndex = (int)module.Source;
        Invoke(window, "UpdateRangeControls");
        if (mode == RangeMode.Cycle)
        {
            var cycle = new CodexQuotaCycle(SeedDay.AddHours(9.5), SeedDay.AddDays(2), SeedDay.AddDays(2), 0, null, false);
            var codex = (CodexUsageModule)module;
            codex.QuotaCycles = new[] { cycle };
            codex.SelectedCycle = cycle;
            var combo = Get<ComboBox>(window, "CycleBox");
            combo.ItemsSource = codex.QuotaCycles;
            combo.SelectedItem = cycle;
        }
    }

    private static void SeedCaches()
    {
        var folders = new[] { "CodexTokenMonitor", "ClaudeCodeTokenMonitor", "ZCodeTokenMonitor", "WorkBuddyTokenMonitor", "DshTokenMonitor" };
        foreach (var source in Enum.GetValues<UsageSource>())
        {
            var multiplier = (int)source + 1;
            var events = new[]
            {
                new TokenUsageEvent(SeedDay.AddHours(9), 100 * multiplier, 20 * multiplier, 10 * multiplier, 0, 110 * multiplier, $"{source}:one"),
                new TokenUsageEvent(SeedDay.AddHours(10), 200 * multiplier, 40 * multiplier, 20 * multiplier, 0, 220 * multiplier, $"{source}:two"),
                new TokenUsageEvent(SeedDay.AddDays(1).AddHours(9), 500 * multiplier, 100 * multiplier, 50 * multiplier, 0, 550 * multiplier, $"{source}:three")
            };
            var cache = UsageCacheStore.Load(folders[(int)source]);
            foreach (var group in events.GroupBy(item => item.Timestamp.Date))
            {
                var day = new DateTimeOffset(group.Key, TimeSpan.FromHours(8));
                var bucket = new TokenUsageBucket { StartLocal = day };
                foreach (var item in group) bucket.Add(item);
                cache.Put(bucket, isComplete: true, scannedThroughLocal: day.AddDays(1).AddTicks(-1), detailEvents: group.ToArray());
            }
            cache.Save();
        }
    }

    private static MainWindow CreateWindow()
    {
        var window = new MainWindow();
        Windows.Add(window);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ClosedSignals.Add(window, closed);
        window.Loaded += (_, _) => LoadedWindows.Add(window);
        window.Closed += (_, _) => closed.TrySetResult();
        Get<DispatcherTimer>(window, "refreshTimer").Stop();
        SetSuppressed(window, true);
        Get<CheckBox>(window, "AutoRefreshBox").IsChecked = false;
        return window;
    }

    /// <summary>
    /// Holds the window's programmatic-update suppression the way the old
    /// initializing boolean did: idempotent on re-entry, one release per hold.
    /// </summary>
    private static void SetSuppressed(MainWindow window, bool suppressed)
    {
        if (suppressed)
        {
            if (!Suppressions.ContainsKey(window))
            {
                Suppressions[window] = Get<UiEventSuppressor>(window, "suppressUiEvents").Begin();
            }

            return;
        }

        if (Suppressions.Remove(window, out var scope))
        {
            scope.Dispose();
        }
    }

    private static async Task CloseWindowAsync(MainWindow window)
    {
        var closed = ClosedSignals[window].Task;
        if (!closed.IsCompleted) window.Close();
        await closed.WaitAsync(TimeSpan.FromSeconds(10));
        if (GetObject(window, "shutdownTask") is Task shutdownTask)
            await shutdownTask.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static async Task RenderContentAsync(MainWindow window, string path)
    {
        await DrainBindingsAsync(window);
        var expectedStatus = Get<TextBlock>(window, "StatusText").Text;
        var expectedContentVisibility = Get<FrameworkElement>(window, "UsageSummaryPanel").Visibility;
        var content = (FrameworkElement)window.Content;
        window.Content = null;
        var host = new Window
        {
            Content = content, Width = 1380, Height = 940, Left = -20000, Top = -20000,
            ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
            Background = window.Background, Foreground = window.Foreground,
            FontFamily = window.FontFamily, FontSize = window.FontSize,
            DataContext = window.DataContext
        };
        try
        {
            host.Show();
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Require(ReferenceEquals(content.DataContext, window.DataContext), "render host preserves display DataContext");
            Require(Get<TextBlock>(window, "StatusText").Text == expectedStatus &&
                    Get<FrameworkElement>(window, "UsageSummaryPanel").Visibility == expectedContentVisibility,
                "render host preserves bound display values");
            host.UpdateLayout();
            var bitmap = new RenderTargetBitmap(1380, 940, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(host);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(path);
            encoder.Save(stream);
        }
        finally
        {
            host.Content = null;
            host.Close();
            window.Content = content;
        }
        await DrainBindingsAsync(window);
        CheckDisplayBindings(window);
        Results.Add(new { check = "render", path, width = 1380, height = 940 });
    }

    private static Task Refresh(MainWindow window) => (Task)Invoke(window, "RefreshUsageAsync", true, false)!;
    private static async Task RefreshAndDrainAsync(MainWindow window)
    {
        await Refresh(window).WaitAsync(TimeSpan.FromSeconds(20));
        await DrainBindingsAsync(window);
        CheckDisplayBindings(window);
    }
    private static async Task DrainBindingsAsync(MainWindow window) =>
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);

    private const int DisplayBindingCount = 22;
    private static string DisplayStage(MainWindow window) => GetObject(window.DataContext!, "Stage")!.ToString()!;
    private static void CheckDisplayBindings(MainWindow window)
    {
        static void Bound(DependencyObject target, DependencyProperty property, string name) =>
            Require(BindingOperations.IsDataBound(target, property), name + " retains its XAML binding");
        Bound(window, Window.TitleProperty, "window title");
        foreach (var name in new[] { "TotalValue", "PeriodValue", "InputValue", "CachedValue", "CacheWriteValue", "UncachedValue",
                     "OutputValue", "ReasoningValue", "CacheRatioValue", "EventsValue", "CodingTimeValue", "EmptyUsageTitle", "EmptyUsagePeriod", "EmptyUsageHint", "StatusText" })
            Bound(Get<TextBlock>(window, name), TextBlock.TextProperty, name);
        Bound(Get<TextBlock>(window, "StatusText"), FrameworkElement.ToolTipProperty, "status detail");
        foreach (var name in new[] { "UsageSummaryPanel", "UsageMetricsPanel", "UsageDetailsPanel", "EmptyUsagePanel" })
            Bound(Get<FrameworkElement>(window, name), UIElement.VisibilityProperty, name);
        Bound(Get<Button>(window, "CopySummaryButton"), UIElement.IsEnabledProperty, "copy availability");
    }
    private static T Get<T>(object target, string name) => (T)GetObject(target, name)!;
    private static object? GetObject(object target, string name)
    {
        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        return target.GetType().GetField(name, flags) is { } field
            ? field.GetValue(target)
            : target.GetType().GetProperty(name, flags)!.GetValue(target);
    }
    private static void Set(object target, string name, object? value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static object? Invoke(object target, string name, params object?[] args) => target.GetType()
        .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
        .Single(method => method.Name == name && method.GetParameters().Length == args.Length)
        .Invoke(target, args);
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Probe failed: " + message);
    }

    private static void WriteReport(bool passed, string? error) =>
        ProbeReport.Write(outputRoot, "main", passed, error, Results);
}
