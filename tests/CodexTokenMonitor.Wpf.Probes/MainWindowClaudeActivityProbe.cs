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
        claude.CurrentActivity = new ClaudeCodeActivitySnapshot(now, ClaudeCodeBlockCalculator.Build(rows), today, week, 3);
        Invoke(window, "ApplyClaudeActivitySummary", claude);

        Require(Get<TextBlock>(window, "ClaudeBlockDetail").Text.StartsWith("13:00–18:00", StringComparison.Ordinal),
            "live block opens on the hour of its first request");
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

        static async Task Wait(Func<bool> predicate, string message)
        {
            for (var attempt = 0; attempt < 200 && !predicate(); attempt++) await Task.Delay(50);
            Require(predicate(), message);
        }
    }
}
