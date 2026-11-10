using System.Windows;

namespace CodexTokenMonitor;

public partial class MainWindow
{
    private CodexMessageHistory? messageHistory;
    private CodexRhythmWindow? rhythmWindow;

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
            InteractionCountButton.ToolTip = "打开 Codex 使用节律，在窗口中手动更新交互次数";
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

        var window = new CodexRhythmWindow(() => dayTimeZone.Offset, messageHistory, history =>
        {
            messageHistory = history;
            UpdateInteractionCount(CurrentSource(), GetSelectedRange());
        }) { Owner = this };
        rhythmWindow = window;
        window.Closed += (_, _) => rhythmWindow = null;
        window.Show();
    }
}
