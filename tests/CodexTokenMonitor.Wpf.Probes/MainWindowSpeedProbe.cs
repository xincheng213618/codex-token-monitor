using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexTokenMonitor;

namespace CodexTokenMonitor.Wpf.Probes;

internal static partial class MainWindowProbe
{
    private static readonly Dictionary<string, string> SourceFixtureHashes = new(StringComparer.OrdinalIgnoreCase);
    private static async Task CheckStatusSpeedAsync(MainWindow window, IReadOnlyDictionary<UsageSource, UsageSourceModule> modules)
    {
        var root = Path.Combine(UsageLogPaths.GetOverrideRoot(UsageSource.Codex)!, "sessions"); Directory.CreateDirectory(root);
        var path = Path.Combine(root, "status-speed.jsonl"); var now = BeijingClock.Now;
        string Line(DateTimeOffset time, string type, object payload) => JsonSerializer.Serialize(new { timestamp = time, type, payload }) + "\n";
        string Response(string id, DateTimeOffset end) => Line(end.AddSeconds(-4), "event_msg", new { type = "task_started" }) +
            Line(end, "token_usage_record", new { response_id = id, usage = new { input_tokens = 900, output_tokens = 400 } });
        File.WriteAllText(path, Line(now.AddSeconds(-15), "session_meta", new { id = "status-speed" }) +
            Line(now.AddSeconds(-14), "turn_context", new { model = "speed-fixture" }) + Response("r1", now.AddSeconds(-5)));
        Select(window, modules[UsageSource.Codex], RangeMode.Day);
        var tabs = Get<TabControl>(window, "DetailsTabs");
        Require(tabs.Items.Cast<TabItem>().Select(tab => tab.Header.ToString()).SequenceEqual(new[] { "用量明细", "实时数据" }), "only the lower detail area switches between usage and live data");
        Require(tabs.Padding == new Thickness(0) && tabs.Margin == new Thickness(0) && Get<Border>(window, "DetailsCard").Padding == new Thickness(12, 10, 12, 10),
            "one padded detail card contains the shared tab header and content");
        foreach (var name in new[] { "UsageBreakdownPanel", "RealtimeDataPanel" })
        {
            var page = Get<Border>(window, name);
            Require(page.Padding == new Thickness(0) && page.Margin == new Thickness(0) && page.BorderThickness == new Thickness(0),
                name + " adds no repeated frame or padding inside the shared card");
        }
        Require(window.FindName("SpeedAnalysisButton") is Button && window.OwnedWindows.Count == 0, "full speed analysis remains in toolbar");
        var status = Get<AgentSpeedStatusMonitor>(window, "speedStatus"); var gate = Get<MonitorRuntime>(window, "runtime").SharedIoGate;
        await gate.WaitAsync();
        try
        {
            Invoke(window, "StartSpeedStatus");
            await Wait(() => status.LastSnapshot?.Requests == 1 && !status.IsReading, "default-page footer sampling bypasses usage gate");
        }
        finally { gate.Release(); }
        Require(tabs.SelectedIndex == 0 && status.IsActive && GetObject(window, "realtimeData") is null,
            "default usage page samples the footer without creating chart or session view");
        Require(status.LastSnapshot is { HasDetails: false, History.Count: 0, Agents.Count: 0, Models.Count: 0 },
            "footer sampling skips chart, session-tree and model-table calculations");
        tabs.SelectedIndex = 1;
        var realtime = Get<RealtimeDataView>(window, "realtimeData");
        await Wait(() => realtime.LastSnapshot is { HasDetails: true, Requests: 1 } && !status.IsReading,
            "selecting live data computes a detailed snapshot even when automatic refresh is paused");
        Require(status.IsActive && realtime.LastSnapshot?.Requests == 1 && Get<TextBlock>(window, "RealtimeSpeedText").Text.StartsWith("实时并行速度 "),
            "live-page sample updates the pure view and footer");
        Get<CheckBox>(window, "AutoRefreshBox").IsChecked = true;
        File.AppendAllText(path, Response("r2", now.AddSeconds(-1)));
        await Wait(() => status.LastSnapshot?.Requests == 2 && !status.IsReading, "live timer reads appended usage");
        Get<CheckBox>(window, "AutoRefreshBox").IsChecked = false;
        File.AppendAllText(path, Response("r3", BeijingClock.Now.AddMilliseconds(-100)));
        await Task.Delay(1200); Require(status.LastSnapshot?.Requests == 2 && !status.IsAutoRefreshing, "checkbox pauses live sampling");
        await status.RefreshAsync(); Require(status.LastSnapshot?.Requests == 3, "manual sampling works on paused live page");
        tabs.SelectedIndex = 0;
        var hiddenViewSnapshot = realtime.LastSnapshot;
        Get<CheckBox>(window, "AutoRefreshBox").IsChecked = true;
        File.AppendAllText(path, Response("r4", BeijingClock.Now.AddMilliseconds(-100)));
        SourceFixtureHashes[path] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
        await Wait(() => status.LastSnapshot?.Requests == 4 && !status.IsReading, "footer continues sampling the new request on the usage page");
        var firstHiddenSample = status.LastSnapshot!;
        Require(firstHiddenSample.Live > 0 && Get<TextBlock>(window, "RealtimeSpeedText").Text.EndsWith($"{firstHiddenSample.Live:N1} tok/s"),
            "lightweight sampling publishes the current nonzero speed to the footer");
        await Wait(() => status.LastSnapshot!.Now > firstHiddenSample.Now && !status.IsReading, "footer continues automatic sampling while the chart page is hidden");
        Require(status.IsActive && status.IsAutoRefreshing && ReferenceEquals(hiddenViewSnapshot, realtime.LastSnapshot),
            "hidden chart and sessions retain their snapshot while the footer advances");
        Require(status.LastSnapshot is { HasDetails: false, History.Count: 0, Agents.Count: 0, Models.Count: 0 },
            "leaving live data also stops generating the hidden presentation data");
        Select(window, modules[UsageSource.ClaudeCode], RangeMode.Day);
        await Wait(() => status.LastSnapshot?.Requests == 0 && !status.IsReading, "hidden-page source change updates the footer");
        Require(ReferenceEquals(hiddenViewSnapshot, realtime.LastSnapshot), "hidden source change does not clear or redraw the realtime view");
        tabs.SelectedIndex = 1;
        await Wait(() => realtime.LastSnapshot is { HasDetails: true, Requests: 0 } && !status.IsReading,
            "returning to live data computes current detail instead of applying a footer-only snapshot");
        Select(window, modules[UsageSource.Codex], RangeMode.Day);
        await Wait(() => status.LastSnapshot?.Requests == 4 && realtime.LastSnapshot?.Requests == 4 && !status.IsReading, "visible source return restores native data");
        Get<CheckBox>(window, "AutoRefreshBox").IsChecked = false;
        Results.Add(new { check = "status-speed-refresh", sharedGateHeld = true, automaticAppend = true, pauseAndManual = true,
            footerAlwaysSamples = true, hiddenViewNotCreated = true, hiddenViewFrozen = true, inactiveSourceFrozen = true, returnAppliesLatest = true });

        tabs.SelectedIndex = 0;
        await RenderContentAsync(window, Path.Combine(outputRoot, "main-usage-analysis.png"), afterLayout: CheckPageLayout);
        await RenderContentAsync(window, Path.Combine(outputRoot, "main-usage-analysis-narrow.png"), 1060, afterLayout: CheckPageLayout, height: 800);
        tabs.SelectedIndex = 1;
        await Wait(() => status.LastSnapshot?.Requests == 4 && !status.IsReading, "live page renders the retained sample");
        await RenderContentAsync(window, Path.Combine(outputRoot, "main-realtime-data.png"), afterLayout: CheckPageLayout);
        await RenderContentAsync(window, Path.Combine(outputRoot, "main-realtime-data-narrow.png"), 1060, afterLayout: CheckPageLayout, height: 800);

        Select(window, modules[UsageSource.ClaudeCode], RangeMode.Day);
        await Wait(() => status.LastSnapshot?.Requests == 0 && realtime.LastSnapshot?.Requests == 0 && !status.IsReading, "empty source clears the live view");
        Select(window, modules[UsageSource.Codex], RangeMode.Day);
        await Wait(() => status.LastSnapshot?.Requests == 4 && realtime.LastSnapshot?.Requests == 4 && !status.IsReading, "source return restores native data");
        tabs.SelectedIndex = 0;
        new WindowInteropHelper(window).EnsureHandle();
        AgentSpeedWindow? popup = null;
        try
        {
            Get<Button>(window, "SpeedAnalysisButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            popup = Get<AgentSpeedWindow>(window, "speedWindow");
            popup.Left = popup.Top = -20000; popup.ShowInTaskbar = false;
            var analysis = popup.View;
            await popup.RefreshAsync();
            await Wait(() => popup.LastSnapshot?.Requests == 4, "analysis window contains all four native requests");
            Require(((DataGrid)analysis.FindName("ModelGrid")).Items.Count == 1 && analysis.FindName("AgentGrid") is DataGrid, "model and session details live inside analysis window");
            Get<Button>(window, "SpeedAnalysisButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(ReferenceEquals(popup, GetObject(window, "speedWindow")) && window.OwnedWindows.Cast<Window>().OfType<AgentSpeedWindow>().Count() == 1,
                "repeated toolbar command activates the existing analysis window");
            await RenderSpeedPopupAsync(popup, "main-speed-window.png");
            Results.Add(new { check = "speed-analysis-window", sourceReturn = true, emptySourceCleared = true, fullDetails = true,
                equalPageBounds = true, trendIndependent = true, sideSessionList = true, sharedCardPadding = true,
                tabHeaderInsideCard = true, metricsAlwaysVisible = true, noHeaderIcon = true,
                toolbarEntry = true, oneHeaderRow = true, cacheInFooter = true, singleton = true });

            analysis.ShowHistory();
            var history = analysis.HistoryView!;
            await history.IndexAsync().WaitAsync(TimeSpan.FromSeconds(15)); await history.RefreshRowsAsync();
            Require(history.VisibleRows.Sum(row => row.Requests) == 4 && history.IsVisible, "history remains within analysis window");
            Require(window.OwnedWindows.Cast<Window>().OfType<AgentSpeedWindow>().Count() == 1 && popup.OwnedWindows.Count == 0,
                "history does not open another window");
            await RenderSpeedPopupAsync(popup, "main-speed-window-history.png");
            analysis.ReturnToLive();
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Require(!history.IsVisible && ((DataGrid)analysis.FindName("ModelGrid")).IsVisible, "return action restores full live analysis");
            var beforeClose = popup.LastSnapshot; var pendingRead = popup.RefreshAsync();
            popup.Close();
            await pendingRead.WaitAsync(TimeSpan.FromSeconds(10));
            await popup.Completion.ContinueWith(_ => { }, TaskScheduler.Default).WaitAsync(TimeSpan.FromSeconds(10));
            Require(ReferenceEquals(beforeClose, popup.LastSnapshot) && GetObject(window, "speedWindow") is null,
                "closed speed window drains and clears the main-window reference without late publication");
            Results.Add(new { check = "speed-analysis-history", persistedRequests = 4, insideAnalysisWindow = true, returnToLive = true,
                workerDrained = true, latePublication = false });
        }
        finally
        {
            if (popup is not null)
            {
                popup.Close();
                await popup.Completion.ContinueWith(_ => { }, TaskScheduler.Default).WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        void CheckPageLayout()
        {
            var selected = tabs.SelectedIndex;
            var sources = Get<TabControl>(window, "SourceTabs");
            var host = Window.GetWindow(sources)!;
            var sourcePoint = sources.TranslatePoint(new Point(), host);
            var dataY = Get<Button>(window, "DataTransferButton").TranslatePoint(new Point(), host).Y;
            var analysisY = Get<Button>(window, "SpeedAnalysisButton").TranslatePoint(new Point(), host).Y;
            var cacheY = Get<Button>(window, "CacheStatusButton").TranslatePoint(new Point(), host).Y;
            tabs.SelectedIndex = 0; host.UpdateLayout();
            var details = Get<Border>(window, "UsageBreakdownPanel");
            var usageBounds = new Rect(details.TranslatePoint(new Point(), host), details.RenderSize);
            var trend = Get<Grid>(window, "TimelineHost");
            var trendBounds = new Rect(trend.TranslatePoint(new Point(), host), trend.RenderSize);
            var timelineHeight = Get<RowDefinition>(window, "TimelineRow").ActualHeight;
            var breakdownHeight = Get<RowDefinition>(window, "BreakdownRow").ActualHeight;
            tabs.SelectedIndex = 1; host.UpdateLayout();
            var livePanel = Get<Border>(window, "RealtimeDataPanel");
            var liveBounds = new Rect(livePanel.TranslatePoint(new Point(), host), livePanel.RenderSize);
            Require(Math.Abs(usageBounds.X - liveBounds.X) < 1 && Math.Abs(usageBounds.Y - liveBounds.Y) < 1 &&
                Math.Abs(usageBounds.Width - liveBounds.Width) < 1 && Math.Abs(usageBounds.Height - liveBounds.Height) < 1,
                $"page switches retain identical outer bounds: {usageBounds} / {liveBounds}");
            var realtimeTrendBounds = new Rect(trend.TranslatePoint(new Point(), host), trend.RenderSize);
            Require(trendBounds == realtimeTrendBounds && Math.Abs(Get<RowDefinition>(window, "TimelineRow").ActualHeight - timelineHeight) < 1 &&
                Math.Abs(Get<RowDefinition>(window, "BreakdownRow").ActualHeight - breakdownHeight) < 1,
                "trend rectangle and splitter row heights are independent of the selected detail page");
            var sessions = (FrameworkElement)realtime.FindName("RecentSessionsPanel");
            var chart = (ContentControl)realtime.FindName("ChartHost");
            var metrics = (FrameworkElement)realtime.FindName("MetricStrip");
            var liveValue = (TextBlock)realtime.FindName("LiveValue");
            Require(metrics.IsVisible && metrics.ActualHeight > 0 && liveValue.IsVisible && liveValue.ActualHeight > 0 &&
                Math.Abs(metrics.TranslatePoint(new Point(), realtime).Y) < 2 &&
                liveValue.TranslatePoint(new Point(0, liveValue.ActualHeight), realtime).Y <= realtime.ActualHeight + 1,
                "live speed remains visible at the top without a redundant heading row, including minimum-height rendering");
            var card = Get<Border>(window, "DetailsCard");
            var cardBounds = new Rect(card.TranslatePoint(new Point(), host), card.RenderSize);
            var tabBounds = new Rect(tabs.TranslatePoint(new Point(), host), tabs.RenderSize);
            var firstTab = (TabItem)tabs.Items[0];
            var headerBounds = new Rect(firstTab.TranslatePoint(new Point(), host), firstTab.RenderSize);
            Require(tabBounds.Left >= cardBounds.Left && tabBounds.Top >= cardBounds.Top &&
                tabBounds.Right <= cardBounds.Right + 1 && tabBounds.Bottom <= cardBounds.Bottom + 1 &&
                headerBounds.Top >= cardBounds.Top && headerBounds.Bottom <= usageBounds.Top + 1,
                "tab header stays inside the fixed detail card above its shared list region");
            var sessionPoint = sessions.TranslatePoint(new Point(), host); var chartPoint = chart.TranslatePoint(new Point(), host);
            Require(realtime.FindName("SessionList") is ListBox && sessions.ActualWidth > 0 && chart.ActualWidth > sessions.ActualWidth &&
                sessionPoint.X >= chartPoint.X + chart.ActualWidth - 1, "recent sessions use a narrow list to the right of the chart");
            Require(Math.Abs(sourcePoint.Y - dataY) < 20 && Math.Abs(sourcePoint.Y - analysisY) < 20, "sources and toolbar occupy a single header row");
            Require(sourcePoint.X <= usageBounds.X + 12, "header icon no longer reserves space before source tabs");
            Require(cacheY >= usageBounds.Bottom - 2, "cache status is below the page area");
            tabs.SelectedIndex = selected; host.UpdateLayout();
        }
        static async Task Wait(Func<bool> predicate, string message)
        {
            var watch = Stopwatch.StartNew();
            while (!predicate()) { if (watch.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException(message); await Task.Delay(20); }
        }
    }

    private static async Task RenderSpeedPopupAsync(Window window, string fileName)
    {
        await Dispatcher.Yield(DispatcherPriority.ContextIdle); window.UpdateLayout();
        var width = (int)Math.Ceiling(window.ActualWidth); var height = (int)Math.Ceiling(window.ActualHeight);
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); image.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        var path = Path.Combine(outputRoot, fileName); using var stream = File.Create(path); encoder.Save(stream);
        Results.Add(new { check = "render", path, width, height });
    }
}
