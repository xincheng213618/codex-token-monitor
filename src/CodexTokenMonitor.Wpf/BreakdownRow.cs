using System.ComponentModel;

namespace CodexTokenMonitor;

internal sealed class BreakdownRow : INotifyPropertyChanged
{
    private static readonly PropertyChangedEventArgs QuotaChanged = new(nameof(Quota));
    private string quota = "";
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Model { get; set; } = "";
    public string ActualCost { get; set; } = "";
    public string Label { get; set; } = "";
    public string Total { get; set; } = "";
    public string Input { get; set; } = "";
    public string Cached { get; set; } = "";
    public string CacheWrite { get; set; } = "";
    public string Uncached { get; set; } = "";
    public string Output { get; set; } = "";
    public IReadOnlyList<string> Prices { get; set; } = Array.Empty<string>();
    public string Quota
    {
        get => quota;
        set
        {
            if (quota == value) return;
            quota = value;
            PropertyChanged?.Invoke(this, QuotaChanged);
        }
    }
}
