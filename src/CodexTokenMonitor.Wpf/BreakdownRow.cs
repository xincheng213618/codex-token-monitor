namespace CodexTokenMonitor;

internal sealed class BreakdownRow
{
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
    public string Quota { get; set; } = "";
}
