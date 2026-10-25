using System.Windows;
using System.Windows.Controls;

namespace CodexTokenMonitor;

public partial class MainWindow
{
    private readonly AgentSpeedStatusMonitor speedStatus = new();
    private AgentSpeedWindow? speedWindow;
    private RealtimeDataView? realtimeData;
    private Task closedSpeedCompletion = Task.CompletedTask;
    private UsageSource? speedStatusSource;
    private UsageSource? realtimeDataSource;
    private bool speedUiReady, speedStatusStarted;

    private void InitializeSpeedStatus()
    {
        speedUiReady = true;
        speedStatus.Updated += snapshot =>
        {
            if (isClosed || !speedStatus.IsActive) return;
            RealtimeSpeedText.Text = $"实时并行速度 {(snapshot.EstimatedShare > 0 ? "≈ " : "")}{snapshot.Live:N1} tok/s";
            RealtimeSpeedText.ToolTip = $"{UsageSourceRegistry.For(CurrentSource()).Title} · 3 秒窗口 · 更新 {snapshot.Now:HH:mm:ss} · 估算占比 {snapshot.EstimatedShare:P0}";
            if (RealtimeDataTab.IsSelected)
            {
                EnsureRealtimeData();
                realtimeData!.Apply(snapshot);
            }
        };
        speedStatus.Failed += error =>
        {
            if (isClosed || !speedStatus.IsActive) return;
            RealtimeSpeedText.Text = "实时并行速度 —";
            RealtimeSpeedText.ToolTip = "采样失败，将重试：" + error.Message;
            if (RealtimeDataTab.IsSelected) realtimeData?.ShowError(error.Message);
        };
        Loaded += (_, _) => StartSpeedStatus();
    }
    private void StartSpeedStatus()
    {
        if (isClosed) return;
        speedStatusStarted = true; UpdateSpeedSelection();
    }
    private void UpdateSpeedSelection()
    {
        if (!speedUiReady || isClosed || !speedStatusStarted) return;
        var sourceChanged = speedStatusSource != CurrentSource();
        if (sourceChanged)
        {
            speedStatusSource = CurrentSource(); RealtimeSpeedText.Text = "实时并行速度 —";
            RealtimeSpeedText.ToolTip = "正在读取 " + UsageSourceRegistry.For(CurrentSource()).Title + " 的实时速度…";
        }
        if (RealtimeDataTab.IsSelected)
        {
            EnsureRealtimeData();
            if (!sourceChanged && speedStatus.LastSnapshot is { } latest) realtimeData!.Apply(latest);
        }
        speedStatus.Activate(CurrentSource(), AutoRefreshBox.IsChecked == true);
    }
    private void EnsureRealtimeData()
    {
        if (realtimeData is null)
        {
            RealtimeDataHost.Content = realtimeData = new();
        }
        if (realtimeDataSource != CurrentSource())
        {
            realtimeDataSource = CurrentSource(); realtimeData.SetSource(CurrentSource());
        }
    }
    private void DetailsTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.Source, DetailsTabs)) UpdateSpeedSelection();
    }
    private void SpeedAnalysisButton_Click(object sender, RoutedEventArgs e)
    {
        if (isClosed) return;
        if (speedWindow is not null) { speedWindow.SelectSource(CurrentSource()); speedWindow.Activate(); return; }
        var window = new AgentSpeedWindow(CurrentSource()) { Owner = this };
        speedWindow = window;
        window.Closed += (_, _) =>
        {
            closedSpeedCompletion = Task.WhenAll(closedSpeedCompletion, window.Completion).ContinueWith(_ => { });
            if (ReferenceEquals(speedWindow, window)) speedWindow = null;
        };
        window.Show();
    }
    private void SpeedAutoRefresh_Changed(object sender, RoutedEventArgs e)
    {
        if (speedUiReady && !isClosed) speedStatus.SetAutoRefresh(AutoRefreshBox.IsChecked == true);
    }
    private Task StopSpeedMonitoring()
    {
        speedStatus.Stop();
        speedWindow?.Close();
        return Task.WhenAll(speedStatus.Completion, closedSpeedCompletion).ContinueWith(_ => { });
    }
}
