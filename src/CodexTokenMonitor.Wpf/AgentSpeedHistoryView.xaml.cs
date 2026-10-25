using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;

namespace CodexTokenMonitor;

public partial class AgentSpeedHistoryView : UserControl
{
    private readonly AgentSpeedHistoryStore store;
    private readonly Func<DateTimeOffset> clock;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? indexing;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMinutes(10) };
    private Task indexTask = Task.CompletedTask, queryTask = Task.CompletedTask;
    private bool ready, closed, busy;
    private long queryVersion;
    private IReadOnlyList<AgentSpeedHistoryRow> visibleRows = [];
    internal IReadOnlyList<AgentSpeedHistoryRow> VisibleRows => visibleRows;
    internal Task Completion => Task.WhenAll(indexTask, queryTask).ContinueWith(_ => { }, TaskScheduler.Default);

    internal AgentSpeedHistoryView(AgentSpeedHistoryStore store, UsageSource? initial,
        Func<DateTimeOffset>? clock = null, bool autoIndex = true)
    {
        this.store = store; this.clock = clock ?? (() => BeijingClock.Now);
        InitializeComponent();
        HistorySourceBox.Items.Add(new ComboBoxItem { Content = "全部来源", Tag = null });
        foreach (var source in UsageSourceRegistry.All) HistorySourceBox.Items.Add(new ComboBoxItem { Content = source.Title, Tag = source.Source });
        HistorySourceBox.SelectedIndex = initial is { } selected ? UsageSourceRegistry.IndexOf(selected) + 1 : 0;
        EndDate.SelectedDate = this.clock().ToOffset(CodexUsageReader.BeijingOffset).Date;
        StartDate.SelectedDate = EndDate.SelectedDate.Value.AddDays(-29);
        ready = true;
        Loaded += async (_, _) => { await RefreshRowsAsync(); if (autoIndex && !closed) { timer.Start(); await IndexAsync(); } };
        timer.Tick += async (_, _) => await IndexAsync();
        Unloaded += (_, _) => timer.Stop();
    }

    internal void Stop()
    {
        if (closed) return;
        closed = true; timer.Stop(); lifetime.Cancel(); indexing?.Cancel();
        _ = Completion.ContinueWith(_ => { indexing?.Dispose(); lifetime.Dispose(); }, TaskScheduler.Default);
    }

    internal void SetSource(UsageSource source) => HistorySourceBox.SelectedIndex = UsageSourceRegistry.IndexOf(source) + 1;
    internal void SetCompact() => HistoryHeading.Visibility = Visibility.Collapsed;

    internal async Task RefreshRowsAsync()
    {
        if (!ready || closed) return;
        if (StartDate.SelectedDate is not { } start || EndDate.SelectedDate is not { } end || end < start)
        { HistoryStatus.Text = "请选择有效日期，结束日期不能早于开始日期。"; return; }
        var source = (HistorySourceBox.SelectedItem as ComboBoxItem)?.Tag as UsageSource?;
        var version = ++queryVersion;
        try
        {
            var task = Task.Run(() => store.Read(DateOnly.FromDateTime(start), DateOnly.FromDateTime(end), source), lifetime.Token);
            queryTask = Task.WhenAll(queryTask, task); var rows = await task;
            if (closed || version != queryVersion) return;
            visibleRows = rows;
            HistoryGrid.ItemsSource = rows.Select(row => new
            {
                Day = row.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Source = UsageSourceRegistry.For(row.Source).Title,
                row.Model, Mode = row.Mode ?? "未知", Coverage = $"{row.MeasuredRequests:N0} / {row.Requests:N0}",
                Output = row.OutputTokens.ToString("N0"), Average = Rate(row.Average(AgentSpeedMetric.Output)), TotalAverage = Rate(row.Average(AgentSpeedMetric.TotalThroughput))
            }).ToArray();
            ExportButton.IsEnabled = rows.Count > 0;
            RangeSummary.Text = $"{start:yyyy-MM-dd} 至 {end:yyyy-MM-dd} · {(source is { } value ? UsageSourceRegistry.For(value).Title : "全部来源")} · {rows.Sum(r => r.Requests):N0} 个请求 · {rows.Sum(r => r.OutputTokens):N0} 输出 Token";
            var measuredMs = rows.Sum(r => (double)r.MeasuredMilliseconds);
            var average = measuredMs > 0 ? rows.Sum(r => (double)r.MeasuredOutput) * 1000 / measuredMs : (double?)null;
            var peak = rows.Select(r => r.OutputPeak).Max();
            AverageSummary.Text = $"平均输出 {Rate(average)} tok/s · 单来源实测峰值 {Rate(peak)} tok/s · {rows.Sum(r => r.Unfinalized):N0} 个响应尚无最终用量；峰值按来源取最大值，不相加。";
            if (!busy) HistoryStatus.Text = "已读取持久历史。更新索引可补入新增日志；CSV 导出当前表格数据。";
        }
        catch (OperationCanceledException) when (closed) { }
        catch (Exception ex) { if (!closed) HistoryStatus.Text = "历史读取失败，已保留上次显示与原文件：" + ex.Message; }
    }

    internal async Task IndexAsync(bool repair = false)
    {
        if (closed || busy) return;
        busy = true; IndexButton.IsEnabled = RepairButton.IsEnabled = false; CancelIndexButton.IsEnabled = true;
        indexing?.Dispose(); indexing = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var token = indexing.Token; string? backup = null;
        try
        {
            var now = clock(); var lastUpdate = DateTime.MinValue;
            var progress = new Progress<AgentSpeedHistoryProgress>(value =>
            {
                if (closed || !busy || DateTime.UtcNow - lastUpdate < TimeSpan.FromMilliseconds(100) && value.Completed < value.Total) return;
                lastUpdate = DateTime.UtcNow;
                HistoryStatus.Text = $"正在索引 {UsageSourceRegistry.For(value.Source).Title}：{value.Completed:N0} / {value.Total:N0} 个变化文件 · {value.Unreadable:N0} 个暂未读完";
            });
            var task = Task.Run(() =>
            {
                if (repair) backup = store.BackupAndReset(token);
                var indexer = new AgentSpeedHistoryIndexer(store); var read = 0; var reused = 0; var unreadable = 0;
                foreach (var source in UsageSourceRegistry.All)
                {
                    token.ThrowIfCancellationRequested();
                    var result = indexer.Index(source.Source, now, token, progress);
                    read += result.Read; reused += result.Reused; unreadable += result.Unreadable;
                }
                return (read, reused, unreadable);
            }, token);
            indexTask = task; var result = await task;
            if (!closed) HistoryStatus.Text = $"索引完成：读取 {result.read:N0}，复用 {result.reused:N0}，暂未读完 {result.unreadable:N0} 个文件。" + (backup is null ? "" : " 原库备份：" + backup);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { if (!closed) HistoryStatus.Text = "索引已取消，已入库历史保留。"; }
        catch (Exception ex) { if (!closed) HistoryStatus.Text = "索引失败，原有历史保留，可重试或备份后重建：" + ex.Message; }
        finally
        {
            busy = false;
            if (!closed)
            {
                IndexButton.IsEnabled = RepairButton.IsEnabled = true; CancelIndexButton.IsEnabled = false;
                var status = HistoryStatus.Text; await RefreshRowsAsync(); HistoryStatus.Text = status;
            }
        }
    }

    private async void Query_Changed(object sender, SelectionChangedEventArgs e) => await RefreshRowsAsync();
    private async void Index_Click(object sender, RoutedEventArgs e) => await IndexAsync();
    private void CancelIndex_Click(object sender, RoutedEventArgs e) => indexing?.Cancel();
    private async void Repair_Click(object sender, RoutedEventArgs e) => await IndexAsync(repair: true);
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (visibleRows.Count == 0) return;
        var dialog = new SaveFileDialog { Filter = "CSV 文件 (*.csv)|*.csv", FileName = "速度历史-" + clock().ToString("yyyyMMdd") + ".csv" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try { File.WriteAllText(dialog.FileName, AgentSpeedHistoryCsv.Build(visibleRows), new UTF8Encoding(true)); HistoryStatus.Text = "已导出当前表格：" + dialog.FileName; }
        catch (Exception ex) { HistoryStatus.Text = "导出失败：" + ex.Message; }
    }
    private static string Rate(double? value) => value?.ToString("N1", CultureInfo.CurrentCulture) ?? "—";
}
