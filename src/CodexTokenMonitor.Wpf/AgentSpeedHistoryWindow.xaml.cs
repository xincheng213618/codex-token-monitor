using System.Windows;

namespace CodexTokenMonitor;

public partial class AgentSpeedHistoryWindow : Window
{
    private readonly AgentSpeedHistoryView view;
    internal IReadOnlyList<AgentSpeedHistoryRow> VisibleRows => view.VisibleRows;
    internal Task Completion => view.Completion;
    internal AgentSpeedHistoryWindow(AgentSpeedHistoryStore store, UsageSource? initial,
        Func<DateTimeOffset>? clock = null, bool autoIndex = true)
    {
        InitializeComponent();
        HistoryHost.Content = view = new(store, initial, clock, autoIndex);
        Closed += (_, _) => view.Stop();
    }
    internal Task RefreshRowsAsync() => view.RefreshRowsAsync();
    internal Task IndexAsync(bool repair = false) => view.IndexAsync(repair);
}