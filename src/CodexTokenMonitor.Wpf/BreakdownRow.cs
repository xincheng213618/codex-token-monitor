namespace CodexTokenMonitor;

internal sealed class BreakdownRow
{
    public string Label { get; set; } = "";
    public string Total { get; set; } = "";
    public string Input { get; set; } = "";
    public string Cached { get; set; } = "";
    public string Uncached { get; set; } = "";
    public string Output { get; set; } = "";
    public string Price1 { get; set; } = "";
    public string Price2 { get; set; } = "";
    public string Price3 { get; set; } = "";
    public string Quota { get; set; } = "";
}
