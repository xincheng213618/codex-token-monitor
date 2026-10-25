using System.Collections.ObjectModel;
using System.ComponentModel;

namespace CodexTokenMonitor;

internal abstract class AgentSpeedTableRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(""));
    protected static string Rate(double? value) => value?.ToString("N1") ?? "—";

    // Reuse row objects so a one-second sample does not reset selection, sorting or scroll position.
    internal static void Sync<TModel, TRow>(ObservableCollection<TRow> rows, IReadOnlyList<TModel> models,
        Func<TModel, string> modelKey, Func<TRow, string> rowKey, Func<TModel, TRow> create, Action<TRow, TModel> update)
    {
        for (var target = 0; target < models.Count; target++)
        {
            var key = modelKey(models[target]); var existing = -1;
            for (var index = target; index < rows.Count; index++) if (rowKey(rows[index]) == key) { existing = index; break; }
            if (existing < 0) rows.Insert(target, create(models[target]));
            else
            {
                if (existing != target) rows.Move(existing, target);
                update(rows[target], models[target]);
            }
        }
        while (rows.Count > models.Count) rows.RemoveAt(rows.Count - 1);
    }
}

internal sealed class AgentSpeedModelRow(AgentSpeedModelSummary value) : AgentSpeedTableRow
{
    private AgentSpeedModelSummary value = value;
    public string Key => Identity(value);
    internal static string Identity(AgentSpeedModelSummary row) => $"{(int)row.Source}|{row.Model.Length}:{row.Model}|{row.ExecutionMode}";
    public string Model => value.Model;
    public string Source => UsageSourceRegistry.For(value.Source).Title;
    public string Mode => value.ExecutionMode ?? "未知";
    public string Coverage => $"{value.MeasuredRequests:N0} / {value.Requests:N0}";
    public string Output => value.OutputTokens.ToString("N0");
    public string Average => Rate(value.Average);
    internal void Update(AgentSpeedModelSummary row) { if (value == row) return; value = row; Changed(); }
}

internal sealed class AgentSpeedSessionRow(AgentSpeedAgentSummary value) : AgentSpeedTableRow
{
    private AgentSpeedAgentSummary value = value;
    public string Key => value.Agent.Id;
    public string Session => new string(' ', value.Depth * 2) + (value.Depth > 0 ? "└ " : value.Agent.IsSubagent ? "子 · " : "") +
        (value.Agent.SessionId.Length > 16 ? value.Agent.SessionId[..16] : value.Agent.SessionId);
    public string Model => value.Agent.Model;
    public string Source => UsageSourceRegistry.For(value.Agent.Source).Title;
    public string Phase => value.Phase switch { AgentSpeedPhase.Generating => "生成中", AgentSpeedPhase.Tools => "工具执行", _ => "空闲" };
    public string Live => value.Live > 0 ? (value.EstimatedShare > 0 ? "≈ " : "") + Rate(value.Live) : "—";
    internal void Update(AgentSpeedAgentSummary row) { if (value == row) return; value = row; Changed(); }
}
