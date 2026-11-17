using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace CodexTokenMonitor;

public partial class MainWindow
{
    private CodexMessageHistory? messageHistory;
    private CodexRhythmWindow? rhythmWindow;
    private readonly DispatcherTimer rhythmTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromHours(1) };
    private bool rhythmRefreshInProgress;
    private string? rhythmRefreshError;

    private void InitializeRhythmRefresh()
    {
        rhythmTimer.Tick += async (_, _) => await RefreshRhythmAsync();
        Loaded += async (_, _) =>
        {
            if (isClosed || rhythmTimer.IsEnabled) return;
            rhythmTimer.Start();
            await RefreshRhythmAsync();
        };
    }

    private async Task RefreshRhythmAsync()
    {
        if (rhythmRefreshInProgress || isClosed || runtime.IsStopping) return;
        rhythmRefreshInProgress = true;
        rhythmRefreshError = null;
        rhythmWindow?.ShowRefreshStatus(true, null);
        try
        {
            await runtime.Run("Codex 使用节律", async token =>
            {
                if (messageHistory is null)
                {
                    var saved = await Task.Run(CodexRhythmReader.ReadSaved, token);
                    if (isClosed) return;
                    if (saved is not null) ApplyRhythmHistory(saved);
                }

                var refreshed = await Task.Run(() => CodexRhythmReader.Read(DateTimeOffset.UtcNow, token), token);
                if (!isClosed) ApplyRhythmHistory(refreshed);
            });
        }
        catch (OperationCanceledException) when (isClosed || runtime.IsStopping) { }
        catch (Exception error)
        {
            Trace.TraceError($"Codex rhythm refresh: {error}");
            rhythmRefreshError = error.Message;
        }
        finally
        {
            rhythmRefreshInProgress = false;
            if (!isClosed) rhythmWindow?.ShowRefreshStatus(false, rhythmRefreshError);
        }
    }

    private void ApplyRhythmHistory(CodexMessageHistory history)
    {
        messageHistory = history;
        UpdateInteractionCount(CurrentSource(), GetSelectedRange());
        rhythmWindow?.ShowHistory(history, dayTimeZone.Offset);
    }

    private void UpdateInteractionCount(UsageSource source, SelectedRange range)
    {
        var codex = source == UsageSource.Codex;
        CacheWriteTile.Visibility = codex ? Visibility.Collapsed : Visibility.Visible;
        InteractionCountButton.Visibility = codex ? Visibility.Visible : Visibility.Collapsed;
        EmptyRhythmButton.Visibility = InteractionCountButton.Visibility;
        if (!codex) return;

        if (messageHistory is null)
        {
            InteractionCountValue.Text = "—";
            InteractionCountButton.ToolTip = "打开 Codex 使用节律。程序运行时每小时更新交互次数。";
            return;
        }

        var history = messageHistory;
        var end = range.End < history.UpdatedAt ? range.End : history.UpdatedAt;
        var count = history.CountInRange(range.Start, end);
        var observed = count > 0 || history.TurnStarts.Any(stamp => stamp >= range.Start && stamp < end);
        InteractionCountValue.Text = observed ? count.ToString("N0") : "—";
        InteractionCountButton.ToolTip = $"当前范围内记录的人工消息 · 更新于 {history.UpdatedAt.ToOffset(dayTimeZone.Offset):MM-dd HH:mm:ss}" +
            (observed ? "\n点击查看详情" : "\n当前范围缺少可确认的会话记录");
    }

    private void InteractionCountButton_Click(object sender, RoutedEventArgs e)
    {
        if (isClosed) return;
        if (rhythmWindow is not null)
        {
            if (rhythmWindow.WindowState == WindowState.Minimized) rhythmWindow.WindowState = WindowState.Normal;
            rhythmWindow.Activate();
            return;
        }

        var window = new CodexRhythmWindow(RefreshRhythmAsync) { Owner = this };
        if (messageHistory is not null) window.ShowHistory(messageHistory, dayTimeZone.Offset);
        window.ShowRefreshStatus(rhythmRefreshInProgress, rhythmRefreshError);
        rhythmWindow = window;
        window.Closed += (_, _) => rhythmWindow = null;
        window.Show();
    }
}
