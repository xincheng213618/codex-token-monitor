using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexTokenMonitor;
using Microsoft.Data.Sqlite;

namespace CodexTokenMonitor.Wpf.Probes;

internal static class SettingsProbe
{
    private static readonly List<object> Results = new();
    private static readonly List<Window> Windows = new();
    private static readonly HashSet<Window> ClosedWindows = new();
    private static readonly byte[] CorruptBytes = Encoding.UTF8.GetBytes("phase4 isolated settings corruption fixture");
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);
    private static string outputRoot = "";
    private static string isolatedRoot = "";
    private static string pricePath = "";
    private static string settingsPath = "";
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
                try
                {
                    window.Close();
                    await WaitUntilAsync(() => ClosedWindows.Contains(window), "cleanup reaches real Closed");
                }
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
        pricePath = SafePath(Path.Combine(MonitorCachePaths.LocalAppData, "CodexTokenMonitor", "price-settings.json"));
        settingsPath = SafePath(MonitorSettingsDatabase.Path);
        foreach (var source in Enum.GetValues<UsageSource>())
            Require(UsageLogPaths.GetOverrideRoot(source)!.StartsWith(logRoot, StringComparison.OrdinalIgnoreCase), "isolated logs");
        Seed();
        using var runtime = new MonitorRuntime();
        suiteRuntime = runtime;
        foreach (var kind in new[] { "PriceSettingsWindow", "SubscriptionPlanWindow", "ResetOpportunityWindow" })
        {
            var dialog = OpenDialog(kind, runtime);
            await WaitLoadedAsync(dialog.Window);
            Require(Get<bool>(dialog.Window, "hasLoadedSettings"), kind + " normal settings loaded");
            Require(Get<Button>(dialog.Window, "SaveButton").IsEnabled, kind + " normal save enabled");
            Require(Get<TextBlock>(dialog.Window, "StatusText").ToolTip is null, kind + " normal tooltip clear");
            Require(ReferenceEquals(Get<AnalysisQuerySession>(dialog.Window, "querySession").Runtime, runtime), "same parent runtime");
            await RenderAsync(dialog.Window, kind + "-healthy.png");
            dialog.Window.Close();
            await dialog.Completion.WaitAsync(Budget);
            Require(ClosedWindows.Contains(dialog.Window), kind + " real Closed");
            Results.Add(new { check = "normal-load", window = kind, saveEnabled = true });
        }

        foreach (var kind in new[] { "PriceSettingsWindow", "SubscriptionPlanWindow", "ResetOpportunityWindow" })
            await CheckFailedLoadAndRetryAsync(kind, runtime);
        foreach (var kind in new[] { "PriceSettingsWindow", "SubscriptionPlanWindow", "ResetOpportunityWindow" })
            await CheckFailedSaveAndRetryAsync(kind, runtime);

        var stop = await runtime.StopAsync(Budget);
        Require(stop.Completed, "runtime drained");
        Require(Windows.All(ClosedWindows.Contains), "every window raised Closed");
        Require(!Directory.EnumerateFiles(logRoot, "*", SearchOption.AllDirectories).Any(), "no source logs touched");
        Results.Add(new { check = "isolation-and-shutdown", isolatedRoot, logRoot, settingsPath, pricePath,
            windowsShown = Windows.Count, allClosed = true, mainWindowCreated = false, accountRequestsSkipped = true,
            sharedNetworkStarted = false, runtimeDrained = true });
    }

    private static void Seed()
    {
        var prices = PriceSettingsStore.Defaults();
        prices.CodexPresets.Insert(0, new PricePreset
        {
            Group = PricePresetGroups.Codex, Provider = "Probe vendor", Model = "phase4-fixture-model",
            CurrencySymbol = "$", UnitLabel = "USD / 1M tokens", Divisor = 1_000_000m,
            UncachedInput = 3.21m, CachedInput = .32m, Output = 6.54m, Source = "isolated UI fixture"
        });
        PriceSettingsStore.Save(prices);
        var now = BeijingClock.Now;
        SubscriptionPlanStore.Save(new[]
        {
            new SubscriptionPlanRecord { Id = "phase4-plan", StartLocal = now.AddDays(-1), EndLocal = now.AddDays(29), PlanName = "Probe plan", AmountCny = 123.45m }
        });
        ResetOpportunityStore.Save(new[]
        {
            new ResetOpportunityRecord { Id = "phase4-reset", GrantedLocal = now.AddDays(-1), ExpiresLocal = now.AddDays(29), Note = "isolated reset fixture" }
        });
        _ = UsageCacheStore.Load();
    }

    private static async Task CheckFailedLoadAndRetryAsync(string kind, MonitorRuntime runtime)
    {
        var path = kind == "PriceSettingsWindow" ? pricePath : settingsPath;
        var healthy = Backup(path);
        var dialog = default(DialogHandle);
        try
        {
            File.WriteAllBytes(path, CorruptBytes);
            dialog = OpenDialog(kind, runtime);
            await WaitLoadedAsync(dialog.Window);
            Require(!Get<bool>(dialog.Window, "hasLoadedSettings"), kind + " fallback never becomes editable");
            Require(!Get<Button>(dialog.Window, "SaveButton").IsEnabled, kind + " save disabled after failed load");
            Require(Get<Button>(dialog.Window, "RetryLoadButton").IsEnabled, kind + " retry available");
            var status = Get<TextBlock>(dialog.Window, "StatusText");
            Require(status.Text.Contains("读取失败") && status.ToolTip is not null, kind + " read warning visible");
            if (kind == "SubscriptionPlanWindow")
            {
                Require(Get<TextBlock>(dialog.Window, "CurrentPlanText").Text == "暂不可用", "failed plan load never says no active plan");
                Require(Get<TextBlock>(dialog.Window, "CurrentPlanDetailText").Text.Contains("读取失败"), "plan header explains read failure");
                Require(Get<TextBlock>(dialog.Window, "PlanRecordCountText").Text == "读取失败", "failed plan load never says zero rows");
            }
            else if (kind == "ResetOpportunityWindow")
            {
                Require(Get<TextBlock>(dialog.Window, "AvailableCountText").Text == "暂不可用", "failed reset load never reports available count");
                Require(Get<TextBlock>(dialog.Window, "ExpirySummaryText").Text == "读取失败", "reset expiry explains read failure");
                Require(Get<TextBlock>(dialog.Window, "ResetRecordCountText").Text == "读取失败", "failed reset load never says zero rows");
            }
            Invoke(dialog.Window, "SaveButton_Click", dialog.Window, new RoutedEventArgs());
            Invoke(dialog.Window, kind == "PriceSettingsWindow" ? "RestoreButton_Click" : "DefaultsButton_Click", dialog.Window, new RoutedEventArgs());
            // Account import/sync is intentionally never invoked: a regression
            // in its UI guard must not give this fixture access to real auth or
            // a network service. Assert its disabled entry point instead.
            if (kind == "SubscriptionPlanWindow") Require(FindButton(dialog.Window, "从 Codex 导入") is { IsEnabled: false }, "account import disabled after failed load");
            if (kind == "ResetOpportunityWindow") Require(FindButton(dialog.Window, "从 Codex 同步") is { IsEnabled: false }, "network sync disabled after failed load");
            Require(ReadBytes(path).SequenceEqual(CorruptBytes), "disabled actions preserve corrupt file bytes");
            Require(!Get<bool>(dialog.Window, "isSaving"), "failed-load save request rejected");
            await RenderAsync(dialog.Window, kind + "-load-failure.png");
            Restore(path, healthy);
            await ((Task)Invoke(dialog.Window, "LoadAsync")!).WaitAsync(Budget);
            Require(Get<bool>(dialog.Window, "hasLoadedSettings") && Get<Button>(dialog.Window, "SaveButton").IsEnabled, kind + " retry recovers");
            Require(Get<TextBlock>(dialog.Window, "StatusText").ToolTip is null, kind + " retry clears warning");
            Require(Get<Button>(dialog.Window, "RetryLoadButton").Visibility == Visibility.Collapsed, "retry hidden after success");
            Require(HasFixtureRow(dialog.Window, kind), kind + " original data recovered");
            if (kind != "PriceSettingsWindow")
            {
                var grid = Get<DataGrid>(dialog.Window, GridName(kind));
                var source = grid.ItemsSource;
                var row = FixtureRow(dialog.Window, kind);
                if (kind == "SubscriptionPlanWindow") Set(row, "AmountText", "333.33");
                else Set(row, "Note", "reload failure preserves unsaved edit");
                var headerName = kind == "SubscriptionPlanWindow" ? "CurrentPlanText" : "AvailableCountText";
                var header = Get<TextBlock>(dialog.Window, headerName).Text;
                _ = Backup(path);
                File.WriteAllBytes(path, CorruptBytes);
                await ((Task)Invoke(dialog.Window, "LoadAsync")!).WaitAsync(Budget);
                Require(ReferenceEquals(source, grid.ItemsSource) && ReferenceEquals(row, FixtureRow(dialog.Window, kind)), "failed reload preserves prior row objects");
                Require(kind == "SubscriptionPlanWindow" ? Get<string>(row, "AmountText") == "333.33" : Get<string>(row, "Note") == "reload failure preserves unsaved edit", "failed reload preserves unsaved editing");
                Require(Get<TextBlock>(dialog.Window, headerName).Text == header && header != "暂不可用", "failed reload preserves prior summary");
                await RenderAsync(dialog.Window, kind + "-reload-failure.png");
                Restore(path, healthy);
                await ((Task)Invoke(dialog.Window, "LoadAsync")!).WaitAsync(Budget);
                Require(Get<TextBlock>(dialog.Window, "StatusText").ToolTip is null, "second recovery clears warning");
            }
            Results.Add(new { check = "failed-load-and-retry", window = kind, staticCacheDidNotMaskFailure = true,
                saveAndOverwriteDisabled = true, corruptBytesPreserved = true, originalRowsRecovered = true, tooltipCleared = true,
                initialHeaderNotMisleading = true, reloadRetainsEdits = kind != "PriceSettingsWindow" ? (bool?)true : null });
        }
        finally
        {
            Restore(path, healthy);
            if (dialog is not null && !ClosedWindows.Contains(dialog.Window)) dialog.Window.Close();
            if (dialog is not null) await dialog.Completion.WaitAsync(Budget);
        }
    }

    private static async Task CheckFailedSaveAndRetryAsync(string kind, MonitorRuntime runtime)
    {
        var path = kind == "PriceSettingsWindow" ? pricePath : settingsPath;
        var dialog = OpenDialog(kind, runtime);
        await WaitLoadedAsync(dialog.Window);
        Require(Get<bool>(dialog.Window, "hasLoadedSettings"), kind + " ready to edit");
        var grid = Get<DataGrid>(dialog.Window, GridName(kind));
        var source = grid.ItemsSource;
        var row = FixtureRow(dialog.Window, kind);
        if (kind == "PriceSettingsWindow")
        {
            var preset = Get<PricePreset>(row, "Preset").Clone();
            preset.UncachedInput = 4.5678m;
            Invoke(row, "Replace", preset);
        }
        else if (kind == "SubscriptionPlanWindow") Set(row, "AmountText", "246.80");
        else Set(row, "Note", "phase4 unsaved edit retained");
        grid.SelectedItem = row;
        grid.ScrollIntoView(row);
        var healthy = Backup(path);
        try
        {
            File.WriteAllBytes(path, CorruptBytes);
            Invoke(dialog.Window, "SaveButton_Click", dialog.Window, new RoutedEventArgs());
            await WaitUntilAsync(() => !Get<bool>(dialog.Window, "isSaving"), kind + " failed save finishes");
            Require(!ClosedWindows.Contains(dialog.Window) && !dialog.Completion.IsCompleted, kind + " failed save keeps dialog open");
            Require(ReferenceEquals(source, grid.ItemsSource) && ReferenceEquals(row, FixtureRow(dialog.Window, kind)), kind + " row identity preserved");
            Require(HasEditedValue(row, kind), kind + " unsaved value preserved");
            Require(Get<Button>(dialog.Window, "SaveButton").IsEnabled, kind + " can retry save directly");
            var status = Get<TextBlock>(dialog.Window, "StatusText");
            Require(status.Text.Contains("保存失败") && status.ToolTip is not null, kind + " save failure visible");
            Require(ReadBytes(path).SequenceEqual(CorruptBytes), kind + " failed save does not overwrite bad data");
            await RenderAsync(dialog.Window, kind + "-save-failure.png");
        }
        finally { Restore(path, healthy); }

        Invoke(dialog.Window, "SaveButton_Click", dialog.Window, new RoutedEventArgs());
        await WaitUntilAsync(() => dialog.Completion.IsCompleted, kind + " successful retry closes dialog");
        Require(await dialog.Completion.WaitAsync(Budget) == true && ClosedWindows.Contains(dialog.Window), kind + " DialogResult true after actual save");
        using var diagnostics = CacheOperationDiagnostics.Begin();
        var persisted = kind switch
        {
            "PriceSettingsWindow" => PriceSettingsStore.Load(forceReload: true).CodexPresets.Single(item => item.Model == "phase4-fixture-model").UncachedInput == 4.5678m,
            "SubscriptionPlanWindow" => SubscriptionPlanStore.Load(forceReload: true).Single(item => item.Id == "phase4-plan").AmountCny == 246.80m,
            _ => ResetOpportunityStore.Load(forceReload: true).Single(item => item.Id == "phase4-reset").Note == "phase4 unsaved edit retained"
        };
        Require(persisted && diagnostics.Warnings.Count == 0, kind + " edit persisted to healthy storage");
        Results.Add(new { check = "failed-save-and-retry", window = kind, dialogRemainedOpen = true,
            rowIdentityPreserved = true, editPreserved = true, corruptBytesPreserved = true, retryDialogResult = true, persisted = true });
    }

    private static bool HasEditedValue(object row, string kind) => kind switch
    {
        "PriceSettingsWindow" => Get<PricePreset>(row, "Preset").UncachedInput == 4.5678m,
        "SubscriptionPlanWindow" => Get<string>(row, "AmountText") == "246.80",
        _ => Get<string>(row, "Note") == "phase4 unsaved edit retained"
    };
    private static bool HasFixtureRow(Window window, string kind) => FixtureRow(window, kind) is not null;
    private static object FixtureRow(Window window, string kind) =>
        ((IEnumerable)Get<DataGrid>(window, GridName(kind)).ItemsSource).Cast<object>().Single(row =>
            kind == "PriceSettingsWindow" ? Get<PricePreset>(row, "Preset").Model == "phase4-fixture-model" :
            Get<string>(row, "Id") == (kind == "SubscriptionPlanWindow" ? "phase4-plan" : "phase4-reset"));
    private static string GridName(string kind) => kind switch { "PriceSettingsWindow" => "PriceGrid", "SubscriptionPlanWindow" => "PlansGrid", _ => "ResetGrid" };

    private static DialogHandle OpenDialog(string kind, MonitorRuntime runtime)
    {
        var type = typeof(QuotaCostCurveWindow).Assembly.GetType("CodexTokenMonitor." + kind)!;
        var arguments = kind == "PriceSettingsWindow" ? new object?[] { PricePresetGroups.Codex, runtime } : new object?[] { runtime };
        var window = (Window)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, arguments, null)!;
        if (kind == "SubscriptionPlanWindow")
        {
            Set(window, "hasRequestedAccountPlan", true);
            Get<TextBlock>(window, "AccountStatusText").Text = "隔离验证：已跳过账户请求。";
        }
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = window.Top = -20000;
        window.ShowInTaskbar = false;
        window.ShowActivated = false;
        Windows.Add(window);
        window.Closed += (_, _) => ClosedWindows.Add(window);
        var completion = new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Dispatcher.BeginInvoke(new Action(() =>
        {
            try { completion.TrySetResult(window.ShowDialog()); }
            catch (Exception ex) { completion.TrySetException(ex); }
        }));
        return new DialogHandle(window, completion.Task);
    }

    private static Task WaitLoadedAsync(Window window) => WaitUntilAsync(() => window.IsLoaded && !Get<bool>(window, "isLoading"), "window load finishes");

    private static byte[] Backup(string path)
    {
        SafePath(path);
        if (path == settingsPath)
        {
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
                command.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();
        }
        return File.ReadAllBytes(path);
    }

    private static void Restore(string path, byte[] healthy)
    {
        SafePath(path);
        SqliteConnection.ClearAllPools();
        File.WriteAllBytes(path, healthy);
    }

    private static byte[] ReadBytes(string path)
    {
        SafePath(path);
        if (path == settingsPath) SqliteConnection.ClearAllPools();
        return File.ReadAllBytes(path);
    }

    private static string SafePath(string path)
    {
        var full = Path.GetFullPath(path);
        Require(full.StartsWith(isolatedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "fixture remains inside isolated root");
        return full;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string description)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed > Budget) throw new TimeoutException(description);
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

    private static Button? FindButton(DependencyObject parent, string content)
    {
        if (parent is Button button && Equals(button.Content, content)) return button;
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
            if (FindButton(child, content) is { } found) return found;
        return null;
    }

    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static T Get<T>(object target, string name) => (T)(target.GetType().GetField(name, InstanceFlags)?.GetValue(target)
        ?? target.GetType().GetProperty(name, InstanceFlags)?.GetValue(target))!;
    private static void Set(object target, string name, object value)
    {
        if (target.GetType().GetField(name, InstanceFlags) is { } field) field.SetValue(target, value);
        else target.GetType().GetProperty(name, InstanceFlags)!.SetValue(target, value);
    }
    private static object? Invoke(object target, string name, params object?[] arguments) => target.GetType().GetMethod(name, InstanceFlags)!.Invoke(target, arguments);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException("Probe failed: " + message); }
    private static void WriteReport(bool passed, string? error) =>
        ProbeReport.Write(outputRoot, "settings", passed, error, Results);
    private sealed record DialogHandle(Window Window, Task<bool?> Completion);
}
