using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace CodexTokenMonitor;

public partial class CacheDetailsWindow : Window
{
    private readonly BackgroundCacheWarmer warmer;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private CacheWarmStatus status;
    private string lastActivityItem = "";

    internal CacheDetailsWindow(BackgroundCacheWarmer warmer)
    {
        this.warmer = warmer;
        status = warmer.CurrentStatus;
        InitializeComponent();
        DataContext = this;
        warmer.StatusChanged += Warmer_StatusChanged;
        timer.Tick += Timer_Tick;
        timer.Start();
        ApplyStatus(status);
        Closed += (_, _) =>
        {
            timer.Stop();
            timer.Tick -= Timer_Tick;
            warmer.StatusChanged -= Warmer_StatusChanged;
        };
    }

    public ObservableCollection<CacheCategoryRow> CategoryRows { get; } = new();

    public ObservableCollection<string> ActivityRows { get; } = new();

    private void Warmer_StatusChanged(CacheWarmStatus next)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => Warmer_StatusChanged(next));
            return;
        }

        status = next;
        ApplyStatus(next);
    }

    private void ApplyStatus(CacheWarmStatus next)
    {
        PhaseText.Text = next.Phase;
        CurrentItemText.Text = next.CurrentItem == "-" ? "当前没有缓存任务" : $"当前：{next.CurrentItem}";
        OverallProgressBar.Value = next.ProgressPercent;
        OverallProgressText.Text = $"{next.CompletedItems:N0}/{next.TotalItems:N0} 日任务 · {next.ProgressPercent:N1}%";
        ResumeButton.IsEnabled = !next.IsRunning && next.RemainingItems > 0;

        CategoryRows.Clear();
        foreach (var category in next.Categories)
        {
            var stateText = category.RemainingDays == 0
                ? "已完成"
                : string.Equals(category.Key, next.CurrentCategoryKey, StringComparison.OrdinalIgnoreCase)
                    ? "正在处理"
                    : "等待";
            CategoryRows.Add(new CacheCategoryRow(
                category.Name,
                $"{category.CompletedDays:N0} 天",
                $"{category.RemainingDays:N0} 天",
                $"{category.TotalDays:N0} 天",
                stateText));
        }

        if (!string.IsNullOrWhiteSpace(next.CurrentItem) &&
            next.CurrentItem != "-" &&
            !string.Equals(lastActivityItem, next.CurrentItem, StringComparison.Ordinal))
        {
            lastActivityItem = next.CurrentItem;
            ActivityRows.Insert(0, $"{next.UpdatedAt:HH:mm:ss}  {next.CurrentItem}");
            while (ActivityRows.Count > 40)
            {
                ActivityRows.RemoveAt(ActivityRows.Count - 1);
            }
        }

        UpdateRuntimeDetails();
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        UpdateRuntimeDetails();
    }

    private void UpdateRuntimeDetails()
    {
        var now = DateTimeOffset.Now;
        var totalElapsed = status.StartedAt is null ? TimeSpan.Zero : now - status.StartedAt.Value;
        var itemElapsed = status.CurrentItemStartedAt is null ? TimeSpan.Zero : now - status.CurrentItemStartedAt.Value;
        TimingText.Text = $"本轮 {FormatDuration(totalElapsed)} · 当前任务 {FormatDuration(itemElapsed)} · 最近更新 {status.UpdatedAt:HH:mm:ss}";

        try
        {
            using var process = Process.GetCurrentProcess();
            ResourceText.Text = $"内存 {process.WorkingSet64 / 1024d / 1024d:N0} MB · CPU {process.TotalProcessorTime.TotalSeconds:N0}s";
        }
        catch
        {
            ResourceText.Text = "";
        }
    }

    private async void ResumeButton_Click(object sender, RoutedEventArgs e)
    {
        await warmer.WarmNowAsync();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private static string FormatDuration(TimeSpan value)
    {
        if (value.TotalHours >= 1)
        {
            return $"{(int)value.TotalHours}小时{value.Minutes}分";
        }

        if (value.TotalMinutes >= 1)
        {
            return $"{(int)value.TotalMinutes}分{value.Seconds}秒";
        }

        return $"{Math.Max(0, value.Seconds)}秒";
    }
}

public sealed record CacheCategoryRow(
    string Name,
    string Completed,
    string Remaining,
    string Total,
    string State);
