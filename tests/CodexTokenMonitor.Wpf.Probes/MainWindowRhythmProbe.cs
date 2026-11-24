using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CodexTokenMonitor.Wpf.Probes;

internal static partial class MainWindowProbe
{
    private static async Task CheckUsageRhythmAsync(MainWindow window, CodexUsageModule codex)
    {
        var root = Path.Combine(UsageLogPaths.GetCodexHome(), "archived_sessions");
        Directory.CreateDirectory(root);
        var log = Path.Combine(root, "rollout-rhythm.jsonl");
        string Entry(DateTimeOffset timestamp, string message) => JsonSerializer.Serialize(new
        {
            timestamp, type = "event_msg", payload = new { type = "user_message", message }
        });
        var first = SeedDay.AddDays(-1).AddHours(22);
        await File.WriteAllLinesAsync(log,
        [
            JsonSerializer.Serialize(new { timestamp = first, type = "session_meta", payload = new { id = "rhythm", source = "cli" } }),
            Entry(first, "boundary"),
            Entry(SeedDay.AddHours(8), "first"),
            Entry(SeedDay.AddHours(8).AddMinutes(10), "second"),
            Entry(SeedDay.AddHours(9), "<heartbeat>"),
            Entry(SeedDay.AddDays(1).AddHours(7), "boundary")
        ]);
        SourceFixtureHashes[log] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(log)));

        Select(window, codex, RangeMode.Day);
        await RefreshAndDrainAsync(window);
        new WindowInteropHelper(window).EnsureHandle();
        var count = Get<TextBlock>(window, "InteractionCountValue");
        Require(count.Text == "—" && Get<Border>(window, "CacheWriteTile").Visibility == Visibility.Collapsed &&
                Get<DataGrid>(window, "BreakdownGrid").Columns.All(column => !Equals(column.Header, "Cache Write")),
            "Codex shows an unrefreshed interaction tile in place of cache write");

        var button = Get<Button>(window, "InteractionCountButton");
        button.ApplyTemplate();
        var tile = (Border)button.Template.FindName("Tile", button);
        Require(tile.Background == Brushes.Transparent &&
                Equals(count.Foreground, Get<TextBlock>(window, "InputValue").Foreground),
            "interaction tile uses the same surface and value color as neighboring metrics");
        SeedRhythmTokenDay();
        var openStarted = DateTimeOffset.UtcNow;
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var openClickReturned = DateTimeOffset.UtcNow;
        var detail = window.OwnedWindows.OfType<CodexRhythmWindow>().Single();
        try
        {
            detail.Left = detail.Top = -20000;
            var months = Get<DataGrid>(detail, "MonthGrid");
            var update = Get<Button>(detail, "RefreshButton");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (count.Text != "2" || !update.IsEnabled || LiveRhythmRow(detail) is null)
                await Task.Delay(15, timeout.Token);
            var openCutoff = LiveRhythmRow(detail)!.SnapshotThrough;
            Require(months.Items.Count == 1 && HistoricalRhythmRow(detail).TotalTokens == 210 &&
                    openCutoff >= openStarted && openCutoff <= openClickReturned,
                "opening rhythm refreshes messages and tokens using the entry cutoff");

            var refreshStarted = DateTimeOffset.UtcNow;
            update.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var refreshClickReturned = DateTimeOffset.UtcNow;
            while (!update.IsEnabled || LiveRhythmRow(detail) is null) await Task.Delay(15, timeout.Token);
            var manualCutoff = LiveRhythmRow(detail)!.SnapshotThrough;
            Require(count.Text == "2" && months.Items.Count == 1 &&
                    HistoricalRhythmRow(detail).Messages == 2 &&
                    Get<DataGrid>(detail, "WeekGrid").Items.Count == 1 &&
                    manualCutoff >= refreshStarted && manualCutoff <= refreshClickReturned,
                "manual rhythm refresh updates the selected period count and detail tables");
            RenderRhythm(detail, "rhythm-months.png");
            Get<TabControl>(detail, "RhythmTabs").SelectedIndex = 1;
            await detail.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Require(Get<FrameworkElement>(detail, "SilencePlot").IsVisible, "longest silence chart displays");
            RenderRhythm(detail, "rhythm-silence.png");
            await CheckRhythmWindowAnalysisAsync(detail, timeout.Token);
            detail.ShowTodaySnapshot(null);
            await CheckDailyRhythmUsageAsync(detail, timeout.Token);

            await File.AppendAllLinesAsync(log, [Entry(SeedDay.AddHours(10), "third")]);
            SourceFixtureHashes[log] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(log)));
            var timer = Get<DispatcherTimer>(window, "rhythmTimer");
            Require(timer.Interval == TimeSpan.FromHours(1), "rhythm refresh has a one-hour cadence");
            timer.Interval = TimeSpan.FromMilliseconds(20);
            timer.Start();
            try
            {
                while (count.Text != "3" || !update.IsEnabled) await Task.Delay(15, timeout.Token);
            }
            finally
            {
                timer.Stop();
                timer.Interval = TimeSpan.FromHours(1);
            }
            var saved = CodexRhythmReader.ReadSaved();
            Require(saved?.CountInRange(SeedDay, SeedDay.AddDays(1)) == 3,
                "automatic update persists the new count");
            Require(HistoricalRhythmRow(detail).TokensPerMessage == 70,
                "automatic rhythm refresh recalculates daily tokens per message");
            Require(Get<DateTimeOffset?>(window, "rhythmTodayCutoff") == manualCutoff && LiveRhythmRow(detail) is null,
                "the hourly history refresh leaves the entry cutoff fixed");
            await CheckTodayRhythmSnapshotAsync(detail, timeout.Token);
            Results.Add(new { check = "codex-rhythm", manualInteractions = 2, automaticInteractions = 3, persisted = true,
                dailyTokens = 210, dailyTokensPerMessage = 105, refreshedTokensPerMessage = 70, dailyOverlaySeries = 3,
                oneHourTokens = 30, twelveHourTokens = 100, analysisCharts = 2, openingRefresh = true,
                snapshotMessages = 4, snapshotTokens = 800, snapshotTokensPerMessage = 200 });
        }
        finally { detail.Close(); }
    }

    private static CodexRhythmDailyUsage HistoricalRhythmRow(CodexRhythmWindow detail) =>
        Get<DataGrid>(detail, "DayGrid").Items.Cast<CodexRhythmDailyUsage>()
            .Single(row => row.Date == DateOnly.FromDateTime(SeedDay.DateTime));

    private static CodexRhythmDailyUsage? LiveRhythmRow(CodexRhythmWindow detail) =>
        Get<DataGrid>(detail, "DayGrid").Items.Cast<CodexRhythmDailyUsage>()
            .SingleOrDefault(row => row.SnapshotThrough.HasValue);

    private static void SeedRhythmTokenDay()
    {
        TokenUsageEvent Event(DateTimeOffset timestamp, long tokens) =>
            new(timestamp, tokens, tokens / 2, 0, 0, tokens);
        TokenUsageEvent[] events =
        [
            Event(SeedDay.AddHours(8).AddTicks(-1), 60),
            Event(SeedDay.AddHours(8), 10),
            Event(SeedDay.AddHours(9).AddTicks(-1), 20),
            Event(SeedDay.AddHours(9), 30),
            Event(SeedDay.AddHours(20).AddTicks(-1), 40),
            Event(SeedDay.AddHours(20), 50)
        ];
        var bucket = new TokenUsageBucket { StartLocal = SeedDay };
        foreach (var item in events) bucket.Add(item);

        var cache = UsageCacheStore.Load("CodexTokenMonitor");
        cache.Put(bucket, scannedThroughLocal: SeedDay.AddDays(1).AddTicks(-1),
            detailEvents: events, propagateErrors: true);
    }

    private static async Task CheckRhythmWindowAnalysisAsync(CodexRhythmWindow detail, CancellationToken token)
    {
        var tabs = Get<TabControl>(detail, "RhythmTabs");
        var analysisTab = Get<TabItem>(detail, "AnalysisTab");
        tabs.SelectedItem = analysisTab;
        var analysis = Get<DataGrid>(detail, "AnalysisGrid");
        while (analysis.Items.Count == 0) await Task.Delay(15, token);

        var points = analysis.Items.Cast<CodexRhythmWindowPoint>().ToDictionary(point => point.Hours);
        Require(points.Count == 7 && points.Values.All(point => point.MessageSampleDays == 1 && point.TokenSampleDays == 1) &&
                points[1].MedianMessagesPerHour == 2 && points[1].MedianTokens == 30 &&
                points[12].MedianTokens == 100 && points[24].MedianTokens == 210,
            "rhythm analysis pairs usage with the busiest half-open human message windows");
        await detail.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle, token);
        var frequency = Get<ScottPlot.WPF.WpfPlot>(detail, "InteractionRatePlot");
        var tokens = Get<ScottPlot.WPF.WpfPlot>(detail, "WindowTokenPlot");
        Require(frequency.IsVisible && tokens.IsVisible && frequency.Plot.GetPlottables().Any() &&
                tokens.Plot.GetPlottables().Any(), "rhythm analysis renders both curves");
        RenderRhythm(detail, "rhythm-analysis.png");
    }

    private static async Task CheckDailyRhythmUsageAsync(CodexRhythmWindow detail, CancellationToken token)
    {
        var days = Get<DataGrid>(detail, "DayGrid");
        var columns = days.Columns.OfType<DataGridTextColumn>().ToDictionary(column => (string)column.Header);
        var day = HistoricalRhythmRow(detail);
        static string? Format(Binding binding, object value) =>
            binding.Converter?.Convert(value, typeof(string), binding.ConverterParameter, CultureInfo.InvariantCulture) as string;
        Require(columns["当日 Token"].Binding is Binding { Path.Path: "TotalTokens", Converter: not null } totalBinding &&
                columns["Token/次"].Binding is Binding { Path.Path: "TokensPerMessage", Converter: not null } ratioBinding &&
                day.Messages == 2 && day.TotalTokens == 210 && day.TokensPerMessage == 105 &&
                Format(totalBinding, 1_250_000_000L) == "1.25B" && Format(ratioBinding, 1_250_000d) == "1.25M",
            "daily rhythm keeps numeric token values and formats compact M/B units");

        var tabs = Get<TabControl>(detail, "RhythmTabs");
        tabs.SelectedItem = Get<TabItem>(detail, "DailyTrendTab");
        await detail.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle, token);
        days.SelectedItem = day;
        var mode = Get<ComboBox>(detail, "DailyTrendMode");
        var overlay = Get<ScottPlot.WPF.WpfPlot>(detail, "DailyOverlayPlot");
        var date = SeedDay.DateTime.ToOADate();

        void CheckDailyOverlay()
        {
            var series = overlay.Plot.GetPlottables<ScottPlot.Plottables.Scatter>().ToArray();
            Require(overlay.IsVisible && series.Length == 3 && series.All(line =>
            {
                var points = line.Data.GetScatterPoints().Where(point => double.IsFinite(point.Y)).ToArray();
                return points.Length == 1 && points[0].X == date && points[0].Y == 100;
            }), "the daily overlay displays all three series on the same date and index scale");
            var marker = overlay.Plot.GetPlottables<ScottPlot.Plottables.VerticalLine>().Single();
            Require(marker.IsVisible && marker.X == date, "the daily overlay marks the selected table date");
        }

        Require(mode.SelectedIndex == 0, "daily rhythm opens in daily overlay mode");
        CheckDailyOverlay();
        mode.SelectedIndex = 1;
        await detail.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle, token);
        Require(!overlay.IsVisible && ReferenceEquals(HistoricalRhythmRow(detail), day) &&
                ReferenceEquals(days.SelectedItem, day), "7-day mode waits for complete windows and retains the daily table");

        mode.SelectedIndex = 0;
        await detail.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle, token);
        CheckDailyOverlay();
        RenderRhythm(detail, "rhythm-daily-usage.png");
    }

    private static async Task CheckTodayRhythmSnapshotAsync(CodexRhythmWindow detail, CancellationToken token)
    {
        var today = SeedDay.AddDays(7);
        var cutoff = today.AddHours(12).AddMinutes(34).AddSeconds(56);
        var date = DateOnly.FromDateTime(today.DateTime);
        var messages = Enumerable.Range(-1, 9).Select(day => SeedDay.AddDays(day).AddHours(8)).ToArray();
        var fullDays = Enumerable.Range(0, 7).ToDictionary(day => DateOnly.FromDateTime(SeedDay.AddDays(day).DateTime), _ => 100L);
        detail.ShowHistory(new(messages, [], cutoff, "probe", 0), SeedDay.Offset);
        detail.ShowAnalysis(null, fullDays);
        detail.ShowTodaySnapshot(new(cutoff, new(date, 4, 2, 0.5, null), 800));
        await detail.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle, token);

        var days = Get<DataGrid>(detail, "DayGrid");
        var snapshot = days.Items[0] as CodexRhythmDailyUsage;
        Require(days.Items.Count == 8 && snapshot is { Messages: 4, TotalTokens: 800, TokensPerMessage: 200 } &&
                snapshot.Date == date && snapshot.SnapshotThrough == cutoff && snapshot.DateLabel == "今日 12:34:56" &&
                days.Columns[0] is DataGridTextColumn { Binding: Binding { Path.Path: "DateLabel" }, SortMemberPath: "Date" },
            "today appears before completed dates with its as-of message count, token usage, and ratio");

        var overlay = Get<ScottPlot.WPF.WpfPlot>(detail, "DailyOverlayPlot");
        ScottPlot.Coordinates[] PlottedPoints() => overlay.Plot.GetPlottables<ScottPlot.Plottables.Scatter>()
            .SelectMany(line => line.Data.GetScatterPoints()).Where(point => double.IsFinite(point.Y)).ToArray();
        var todayX = today.DateTime.ToOADate();
        var yesterdayX = today.AddDays(-1).DateTime.ToOADate();
        Require(overlay.IsVisible && PlottedPoints().Where(point => point.X == todayX).Select(point => point.Y)
                .Order().SequenceEqual(new[] { 200d, 400d, 800d }),
            "the daily overlay includes all three partial-day values using complete-day baselines");
        RenderRhythm(detail, "rhythm-today-snapshot.png");

        var mode = Get<ComboBox>(detail, "DailyTrendMode");
        mode.SelectedIndex = 1;
        await detail.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle, token);
        var completePoints = PlottedPoints();
        Require(overlay.IsVisible && completePoints.Count(point => point.X == yesterdayX) == 3 &&
                completePoints.All(point => point.X != todayX) && ReferenceEquals(days.Items[0], snapshot),
            "7-day curves exclude partial today without removing the pinned today row");
    }

    private static void RenderRhythm(Window window, string filename)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(outputRoot, filename));
        encoder.Save(output);
    }
}
