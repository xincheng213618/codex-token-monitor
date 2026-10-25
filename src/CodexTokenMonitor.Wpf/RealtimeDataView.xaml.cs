using System.Collections.ObjectModel;
using System.Windows.Controls;

namespace CodexTokenMonitor;

/// <summary>Renders the main status sampler's snapshot; owns no timer, watcher or reader.</summary>
public partial class RealtimeDataView : UserControl
{
    private readonly AgentSpeedChart chart = new();
    private readonly ObservableCollection<AgentSpeedSessionRow> sessions = new();
    internal AgentSpeedSnapshot? LastSnapshot { get; private set; }
    public RealtimeDataView()
    {
        InitializeComponent(); ChartHost.Content = chart; SessionList.ItemsSource = sessions;
    }
    internal void SetSource(UsageSource source)
    {
        ToolTip = UsageSourceRegistry.For(source).Title + " · 实时数据";
        LastSnapshot = null; sessions.Clear(); chart.SetData([]);
        LiveValue.Text = LastValue.Text = AverageValue.Text = PeakValue.Text = CoverageValue.Text = "—";
        SampleStatus.Text = "等待采样 · 今日 UTC+8 · 请求耗时含首字等待";
    }
    internal void Apply(AgentSpeedSnapshot snapshot)
    {
        LastSnapshot = snapshot;
        LiveValue.Text = Rate(snapshot.Live); LastValue.Text = Rate(snapshot.Last?.OutputPerSecond);
        AverageValue.Text = Rate(snapshot.Average); PeakValue.Text = Rate(snapshot.MeasuredPeak);
        CoverageValue.Text = $"{snapshot.MeasuredRequests:N0} / {snapshot.Requests:N0}";
        chart.SetData(snapshot.History);
        AgentSpeedTableRow.Sync(sessions, snapshot.Agents, row => row.Agent.Id, row => row.Key,
            row => new AgentSpeedSessionRow(row), (row, model) => row.Update(model));
        SampleStatus.Text = $"更新 {snapshot.Now:HH:mm:ss} · 估算占比 {snapshot.EstimatedShare:P0} · 今日 UTC+8 · 绿色实测 / 橙色含估算" +
            (snapshot.UnreadableFiles > 0 ? $" · {snapshot.UnreadableFiles} 个文件暂未读完" : "");
    }
    internal void ShowError(string error) => SampleStatus.Text = "读取失败，可刷新重试：" + error;
    private static string Rate(double? value) => value is { } rate ? $"{rate:N1} tok/s" : "—";
}
