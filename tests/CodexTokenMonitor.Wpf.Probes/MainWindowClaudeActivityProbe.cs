using System.IO;
using System.Windows;
using System.Windows.Controls;
using CodexTokenMonitor;

namespace CodexTokenMonitor.Wpf.Probes;

internal static partial class MainWindowProbe
{
    private static async Task CheckClaudeActivityPanelAsync(MainWindow window, IReadOnlyDictionary<UsageSource, UsageSourceModule> modules)
    {
        var claude = (ClaudeCodeUsageModule)modules[UsageSource.ClaudeCode];
        Select(window, claude, RangeMode.Day);
        await RefreshAndDrainAsync(window);
        // The page refresh also reads the isolated (empty) transcripts; let it
        // finish so the fixture below is not replaced by that live read.
        await Wait(() => !Get<bool>(window, "isQuotaRefreshing"), "Claude activity refresh settles");

        Invoke(window, "ApplyQuotaSummary", claude, null);
        Require(Get<Border>(window, "ClaudeActivityPanel").Visibility == Visibility.Visible &&
                Get<Border>(window, "QuotaPanel").Visibility == Visibility.Collapsed,
            "Claude Code shows its own header panel instead of the Codex quota panel");

        var now = SeedDay.AddHours(15).AddMinutes(20);
        TokenUsageBucket Row(DateTimeOffset at, long tokens, string model)
        {
            var row = new TokenUsageBucket { StartLocal = at };
            row.Add(new TokenUsageEvent(at, tokens, tokens * 9 / 10, Math.Max(1, tokens / 50), 0, tokens + tokens / 50,
                $"probe-{at.Ticks}", ModelId: model));
            return row;
        }
        var rows = Enumerable.Range(0, 30)
            .Select(index => Row(SeedDay.AddHours(13).AddMinutes(12 + index * 4), 380_000, index % 5 == 0 ? "mimo-v2.5-pro" : "claude-opus-5-5"))
            .Prepend(Row(SeedDay.AddHours(7).AddMinutes(5), 2_000_000, "claude-opus-5-5"))
            .ToList();
        var today = new TokenUsageBucket { StartLocal = SeedDay };
        foreach (var row in rows) today.MergeFrom(row);
        var week = new TokenUsageBucket { StartLocal = SeedDay.AddDays(-6) };
        week.MergeFrom(today);
        week.MergeFrom(Row(SeedDay.AddDays(-2), 48_000_000, "claude-opus-5-5"));
        var blocks = ClaudeCodeBlockCalculator.Build(rows);
        TokenUsageBucket Since(DateTimeOffset start)
        {
            var bucket = new TokenUsageBucket { StartLocal = start };
            foreach (var row in rows.Where(row => row.StartLocal >= start)) bucket.MergeFrom(row);
            return bucket;
        }
        var inferred = new ClaudeQuotaWindowUsage(ClaudeQuotaWindowKind.FiveHour, ClaudeWindowSource.Inferred,
            blocks[^1].StartLocal, blocks[^1].EndLocal, Since(blocks[^1].StartLocal), null, true);
        claude.CurrentActivity = new ClaudeCodeActivitySnapshot(now, blocks, today, week, 3, FiveHourWindow: inferred);
        Invoke(window, "ApplyClaudeActivitySummary", claude);
        Require(Get<TextBlock>(window, "ClaudeFiveHourCapacityValue").Text == "未录入", "capacity waits for a /usage reading");
        Require(Get<TextBlock>(window, "ClaudeBlockDetail").Text.StartsWith("推算 · 13:12–18:12", StringComparison.Ordinal),
            "inferred window opens at its first request, not on the hour");

        // Two 5h windows ($80 and $100 at full) and one week reading.
        foreach (var reading in new[]
                 {
                     ClaudeQuotaEstimator.CreateReading(ClaudeQuotaWindowKind.FiveHour, 25, SeedDay.AddHours(13), SeedDay.AddHours(12), new[] { Row(SeedDay.AddHours(9), 0, "claude-opus-5-5") }) with { CostUsd = 20m, Tokens = 30_000_000 },
                     ClaudeQuotaEstimator.CreateReading(ClaudeQuotaWindowKind.FiveHour, 40, SeedDay.AddHours(18), now, rows) with { CostUsd = 40m, Tokens = 50_000_000 },
                     ClaudeQuotaEstimator.CreateReading(ClaudeQuotaWindowKind.Week, 12, SeedDay.AddDays(4).AddHours(20), now, rows) with { CostUsd = 96m, Tokens = 140_000_000 }
                 })
        {
            ClaudeQuotaReadingStore.Add(reading);
        }
        claude.QuotaReadings = ClaudeQuotaReadingStore.Load();
        Invoke(window, "ApplyClaudeActivitySummary", claude);
        Require(Get<TextBlock>(window, "ClaudeFiveHourCapacityValue").Text == "$90" &&
                Get<TextBlock>(window, "ClaudeWeekValue").Text == "$800",
            "capacity cards show the median full-window dollars");

        // Official status-line windows replace the inferred block everywhere.
        claude.CurrentActivity = claude.CurrentActivity with
        {
            FiveHourWindow = new ClaudeQuotaWindowUsage(ClaudeQuotaWindowKind.FiveHour, ClaudeWindowSource.Official,
                SeedDay.AddHours(13), SeedDay.AddHours(18), Since(SeedDay.AddHours(13)), 40, true),
            WeekWindow = new ClaudeQuotaWindowUsage(ClaudeQuotaWindowKind.Week, ClaudeWindowSource.Official,
                SeedDay.AddDays(-3).AddHours(20), SeedDay.AddDays(4).AddHours(20), week, 12, true)
        };
        Invoke(window, "ApplyClaudeActivitySummary", claude);
        Require(Get<TextBlock>(window, "ClaudeBlockValue").Text == "已用 40%" &&
                Get<TextBlock>(window, "ClaudeFiveHourCapacityDetail").Text.Contains("已用 40%（官方）", StringComparison.Ordinal) &&
                Get<TextBlock>(window, "ClaudeWeekDetail").Text.Contains("已用 12%（官方）", StringComparison.Ordinal),
            "official percentages drive the window and capacity cards");
        var dialog = new ClaudeQuotaWindow(SeedDay.AddHours(18)) { Left = -20000, Top = -20000, ShowInTaskbar = false };
        try
        {
            dialog.Show();
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            Require(((DataGrid)dialog.FindName("HistoryGrid")).Items.Count == 3, "dialog lists every reading");
            await RenderDialogAsync(dialog, Path.Combine(outputRoot, "claude-quota-dialog.png"));
        }
        finally
        {
            dialog.Close();
        }

        Require(Get<TextBlock>(window, "ClaudeModelValue").Text.StartsWith("Opus 5.5 ", StringComparison.Ordinal),
            "dominant model leads the model mix");
        Require(!Get<TextBlock>(window, "ClaudeBlockCostValue").Text.Contains("Credits", StringComparison.Ordinal),
            "plan-unit prices are not added into the dollar headline");
        await RenderContentAsync(window, Path.Combine(outputRoot, "claude-activity.png"));
        await RenderContentAsync(window, Path.Combine(outputRoot, "claude-activity-narrow.png"), 1060, height: 800);

        claude.CurrentActivity = new ClaudeCodeActivitySnapshot(SeedDay.AddDays(1).AddHours(2), ClaudeCodeBlockCalculator.Build(rows), new TokenUsageBucket(), week, 3);
        Invoke(window, "ApplyClaudeActivitySummary", claude);
        Require(Get<TextBlock>(window, "ClaudeBlockValue").Text == "空闲", "an expired block reads as idle");
        await RenderContentAsync(window, Path.Combine(outputRoot, "claude-activity-idle.png"));

        Invoke(window, "ApplyQuotaSummary", modules[UsageSource.Codex], null);
        Require(Get<Border>(window, "ClaudeActivityPanel").Visibility == Visibility.Collapsed, "panel hides on other sources");
        Results.Add(new { check = "claude-activity-panel", liveBlock = true, idleBlock = true, hiddenElsewhere = true });

        static async Task RenderDialogAsync(Window dialog, string path)
        {
            dialog.Measure(new Size(860, 720));
            dialog.Arrange(new Rect(0, 0, 860, 720));
            dialog.UpdateLayout();
            await dialog.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            var content = (FrameworkElement)dialog.Content;
            var image = new System.Windows.Media.Imaging.RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            image.Render(content);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
            using var stream = File.Create(path);
            encoder.Save(stream);
        }

        static async Task Wait(Func<bool> predicate, string message)
        {
            for (var attempt = 0; attempt < 200 && !predicate(); attempt++) await Task.Delay(50);
            Require(predicate(), message);
        }
    }
}
