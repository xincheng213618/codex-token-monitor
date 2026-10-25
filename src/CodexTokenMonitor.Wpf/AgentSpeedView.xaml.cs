using System.Globalization;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;

namespace CodexTokenMonitor;

public partial class AgentSpeedView : System.Windows.Controls.UserControl
{
    private IAgentSpeedSampler monitor;
    private readonly Dictionary<int, IAgentSpeedSampler> monitors = new();
    private AgentSpeedMetric metric;
    private long selectionVersion;
    private bool initialized;
    private readonly bool observeChanges;
    private readonly Func<DateTimeOffset> clock;
    private readonly AgentSpeedHistoryStore historyStore = new();
    private AgentSpeedHistoryView? historyView;
    private DateTimeOffset nextPeakSave;
    private readonly CancellationTokenSource lifetime = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer changeTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly AgentSpeedChart chart = new();
    private readonly ObservableCollection<AgentSpeedModelRow> modelRows = new();
    private readonly ObservableCollection<AgentSpeedSessionRow> sessionRows = new();
    private bool closed, reading;
    private bool refreshPending;
    private bool automatic = true;
    private Task readTask = Task.CompletedTask, cleanup = Task.CompletedTask;
    internal Task Completion => Task.WhenAll(readTask, cleanup, historyView?.Completion ?? Task.CompletedTask)
        .ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    internal AgentSpeedSnapshot? LastSnapshot { get; private set; }
    internal string DisplayTitle { get; private set; } = "速度分析";
    internal UsageSource? SelectedSource => monitor.Source;
    internal event EventHandler? TitleChanged;
    internal AgentSpeedHistoryView? HistoryView => historyView;

    public AgentSpeedView() : this(new AgentSpeedMonitor()) { }
    internal AgentSpeedView(UsageSource source) : this(new AgentSpeedMonitor(source: source)) { }

    internal AgentSpeedView(IAgentSpeedSampler monitor, Func<DateTimeOffset>? clock = null, bool autoPoll = true)
    {
        this.monitor = monitor;
        observeChanges = autoPoll;
        monitor.Changed += MonitorChanged;
        monitors[Key(monitor.Source)] = monitor;
        this.clock = clock ?? (() => BeijingClock.Now);
        InitializeComponent();
        SourceBox.ItemsSource = UsageSourceRegistry.All.Select(source => new SpeedSourceOption(source.Source, source.Title)).Prepend(new SpeedSourceOption(null, "全部来源")).ToArray();
        SourceBox.DisplayMemberPath = nameof(SpeedSourceOption.Title);
        SelectSource(monitor.Source);
        MetricBox.SelectedIndex = 0;
        initialized = true;
        SetHeading();
        ChartHost.Content = chart;
        ModelGrid.ItemsSource = modelRows; AgentGrid.ItemsSource = sessionRows;
        timer.Tick += async (_, _) => await RefreshAsync();
        changeTimer.Tick += async (_, _) => { changeTimer.Stop(); await RefreshAsync(); };
        Loaded += async (_, _) =>
        {
            if (!autoPoll || closed || !automatic || HistoryLayout.Visibility == Visibility.Visible) return;
            timer.Start();
            await RefreshAsync();
        };
        Unloaded += (_, _) => { timer.Stop(); changeTimer.Stop(); };
    }

    internal void SetAutoRefresh(bool enabled)
    {
        automatic = enabled;
        if (enabled && IsLoaded && observeChanges && !closed && HistoryLayout.Visibility != Visibility.Visible) timer.Start();
        else { timer.Stop(); changeTimer.Stop(); }
    }

    internal void Stop()
    {
        if (closed) return;
        closed = true; timer.Stop(); changeTimer.Stop(); lifetime.Cancel();
        historyView?.Stop();
        var ownedMonitors = monitors.Values.ToArray();
        cleanup = readTask.ContinueWith(_ => { foreach (var owned in ownedMonitors) owned.Dispose(); lifetime.Dispose(); }, TaskScheduler.Default);
    }

    internal async Task RefreshAsync()
    {
        if (closed) return;
        if (reading) { refreshPending = true; return; }
        reading = true;
        try
        {
            var now = clock().ToOffset(CodexUsageReader.BeijingOffset);
            var dayStart = new DateTimeOffset(now.Date, now.Offset);
            var token = lifetime.Token;
            var reader = monitor; var selectedMetric = metric; var version = selectionVersion;
            var savePeak = now >= nextPeakSave;
            var task = Task.Run(() =>
            {
                var snapshot = reader.Read(now, dayStart, token, selectedMetric);
                if (savePeak)
                {
                    if (reader.Source is { } source) historyStore.TryRecordPeak(source, snapshot);
                    else if (reader is AgentSpeedHub hub)
                        foreach (var definition in UsageSourceRegistry.All) historyStore.TryRecordPeak(definition.Source, hub.SourceSnapshot(definition.Source, now, dayStart, selectedMetric));
                }
                return snapshot;
            }, token);
            readTask = task;
            var snapshot = await task;
            if (savePeak) nextPeakSave = now.AddSeconds(30);
            if (!closed && version == selectionVersion) Apply(snapshot);
        }
        catch (OperationCanceledException) when (closed) { }
        catch (Exception ex)
        {
            if (!closed) StatusText.Text = $"读取失败，将自动重试：{ex.Message}";
        }
        finally
        {
            reading = false;
            if (refreshPending && !closed) { refreshPending = false; await RefreshAsync(); }
        }
    }

    internal void Apply(AgentSpeedSnapshot snapshot)
    {
        LastSnapshot = snapshot;
        LiveValue.Text = Rate(snapshot.Live) + " tok/s";
        EstimateText.Text = snapshot.Live > 0 ? $"估算占比 {snapshot.EstimatedShare:P0} · 3 秒窗口"
            : snapshot.Agents.Any(a => a.Phase == AgentSpeedPhase.Generating) ? "生成中，等待有效测速样本" : "当前无生成活动";
        LastValue.Text = Rate(snapshot.Last?.Speed(snapshot.Metric)) + " tok/s";
        LastText.Text = snapshot.Last is { } last ? $"{last.Model} · {last.OutputTokens:N0} Token · " +
            (!last.Final ? "等待最终用量" : last.DurationSeconds is { } duration ? $"{duration:N1}s" : "缺少有效耗时") : "暂无完成记录";
        AverageValue.Text = Rate(snapshot.Average) + " tok/s";
        PeakValue.Text = Rate(snapshot.MeasuredPeak) + " tok/s";
        CoverageValue.Text = $"{snapshot.MeasuredRequests:N0} / {snapshot.Requests:N0}";
        AgentSpeedTableRow.Sync(modelRows, snapshot.Models, AgentSpeedModelRow.Identity, row => row.Key,
            row => new AgentSpeedModelRow(row), (row, model) => row.Update(model));
        AgentSpeedTableRow.Sync(sessionRows, snapshot.Agents, row => row.Agent.Id, row => row.Key,
            row => new AgentSpeedSessionRow(row), (row, model) => row.Update(model));
        chart.SetData(snapshot.History);
        StatusText.Text = $"更新 {snapshot.Now:HH:mm:ss} · 本机 {SourceTitle(monitor.Source)} · 每秒采样 · 今日 {snapshot.DayStart:MM-dd} UTC+8" +
            (snapshot.UnfinalizedRequests > 0 ? $" · {snapshot.UnfinalizedRequests} 个响应尚无最终用量" : "") +
            (snapshot.UnreadableFiles > 0 ? $" · {snapshot.UnreadableFiles} 个日志暂未读完，将自动重试" : "");
    }

    private static string Rate(double? value) => value?.ToString("N1", CultureInfo.CurrentCulture) ?? "—";
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private void History_Click(object sender, RoutedEventArgs e) => ShowHistory();
    internal void ShowHistory()
    {
        if (closed) return;
        timer.Stop(); changeTimer.Stop();
        if (historyView is null) HistoryHost.Content = historyView = new(historyStore, monitor.Source, clock, observeChanges);
        LiveLayout.Visibility = Visibility.Collapsed; HistoryLayout.Visibility = Visibility.Visible;
    }
    internal void ReturnToLive()
    {
        if (closed) return;
        HistoryLayout.Visibility = Visibility.Collapsed; LiveLayout.Visibility = Visibility.Visible;
        SetAutoRefresh(automatic);
        if (observeChanges) _ = RefreshAsync();
    }
    private void ReturnToLive_Click(object sender, RoutedEventArgs e) => ReturnToLive();

    private void SetHeading()
    {
        var title = SourceTitle(monitor.Source);
        HeadingText.Text = title + (metric == AgentSpeedMetric.Output ? " 输出速度" : " 总吞吐量");
        DisplayTitle = title + " 速度分析"; TitleChanged?.Invoke(this, EventArgs.Empty);
        MetricDescription.Text = metric == AgentSpeedMetric.Output ? "输出含推理 Token · 请求耗时包含首字等待 · 今日按 UTC+8 统计" :
            "输入+输出含缓存输入 · 请求耗时包含首字等待 · 吞吐量不代表输出生成速度";
        AverageCaption.Text = metric == AgentSpeedMetric.Output ? "今日平均响应速度" : "今日平均吞吐量";
        AverageDetail.Text = metric == AgentSpeedMetric.Output ? "总实测输出 ÷ 总请求耗时" : "总实测输入+输出 ÷ 总请求耗时";
        ModelSourceColumn.Visibility = AgentSourceColumn.Visibility = monitor.Source is null ? Visibility.Visible : Visibility.Collapsed;
    }
    private async void SourceBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!initialized || closed || SourceBox.SelectedItem is not SpeedSourceOption source) return;
        if (!monitors.TryGetValue(Key(source.Source), out var selected))
        {
            selected = source.Source is { } value ? new AgentSpeedMonitor(source: value) : new AgentSpeedHub();
            monitors[Key(source.Source)] = selected; selected.Changed += MonitorChanged;
        }
        monitor = selected; selectionVersion++; nextPeakSave = default; SetHeading();
        LastSnapshot = null; modelRows.Clear(); sessionRows.Clear(); chart.SetData([]);
        LiveValue.Text = LastValue.Text = AverageValue.Text = CoverageValue.Text = PeakValue.Text = "—";
        StatusText.Text = "正在读取该来源的本机日志…";
        await RefreshAsync();
    }
    private async void MetricBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!initialized || closed) return;
        metric = MetricBox.SelectedIndex == 1 ? AgentSpeedMetric.TotalThroughput : AgentSpeedMetric.Output;
        selectionVersion++; nextPeakSave = default; SetHeading(); await RefreshAsync();
    }
    private void MonitorChanged(object? sender, EventArgs e)
    {
        if (closed || !observeChanges) return;
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (!closed && automatic && IsLoaded && HistoryLayout.Visibility != Visibility.Visible &&
                ReferenceEquals(sender, monitor) && !changeTimer.IsEnabled) changeTimer.Start();
        });
    }
    internal void SelectSource(UsageSource? source) => SourceBox.SelectedItem = SourceBox.Items.Cast<SpeedSourceOption>().Single(option => option.Source == source);
    private static int Key(UsageSource? source) => source is { } value ? (int)value : -1;
    private static string SourceTitle(UsageSource? source) => source is { } value ? UsageSourceRegistry.For(value).Title : "全部来源";
    private sealed record SpeedSourceOption(UsageSource? Source, string Title);
}
