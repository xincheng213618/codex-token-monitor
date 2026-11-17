using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
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
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var detail = window.OwnedWindows.OfType<CodexRhythmWindow>().Single();
        try
        {
            detail.Left = detail.Top = -20000;
            await detail.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var months = Get<DataGrid>(detail, "MonthGrid");
            Require(count.Text == "—" && months.Items.Count == 0, "opening rhythm does not read history");

            var update = Get<Button>(detail, "RefreshButton");
            update.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!update.IsEnabled) await Task.Delay(15, timeout.Token);
            Require(count.Text == "2" && months.Items.Count == 1 &&
                    Get<DataGrid>(detail, "DayGrid").Items.Count == 1 &&
                    Get<DataGrid>(detail, "WeekGrid").Items.Count == 1,
                "manual rhythm refresh updates the selected period count and detail tables");
            RenderRhythm(detail, "rhythm-months.png");
            Get<TabControl>(detail, "RhythmTabs").SelectedIndex = 1;
            await detail.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Require(Get<FrameworkElement>(detail, "SilencePlot").IsVisible, "longest silence chart displays");
            RenderRhythm(detail, "rhythm-silence.png");

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
            Results.Add(new { check = "codex-rhythm", manualInteractions = 2, automaticInteractions = 3, persisted = true });
        }
        finally { detail.Close(); }
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
