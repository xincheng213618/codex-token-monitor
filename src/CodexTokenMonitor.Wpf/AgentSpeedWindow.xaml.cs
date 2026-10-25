using System.Windows;
namespace CodexTokenMonitor;
public partial class AgentSpeedWindow : Window
{
    private readonly AgentSpeedView view;
    internal AgentSpeedView View => view;
    public AgentSpeedWindow() : this(new AgentSpeedMonitor()) { }
    internal AgentSpeedWindow(UsageSource source) : this(new AgentSpeedMonitor(source: source)) { }
    internal AgentSpeedWindow(IAgentSpeedSampler monitor, Func<DateTimeOffset>? clock = null, bool autoPoll = true)
    {
        InitializeComponent();
        SpeedHost.Content = view = new(monitor, clock, autoPoll);
        Title = view.DisplayTitle; view.TitleChanged += (_, _) => Title = view.DisplayTitle;
        Closed += (_, _) => view.Stop();
    }
    internal Task Completion => view.Completion;
    internal AgentSpeedSnapshot? LastSnapshot => view.LastSnapshot;
    internal Task RefreshAsync() => view.RefreshAsync();
    internal void Apply(AgentSpeedSnapshot snapshot) => view.Apply(snapshot);
    internal void SelectSource(UsageSource? source) => view.SelectSource(source);
    public new object FindName(string name) => view.FindName(name) ?? base.FindName(name);
}
