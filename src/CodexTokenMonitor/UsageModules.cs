namespace CodexTokenMonitor;

internal enum RangeMode
{
    Day,
    Week,
    Month,
    Cycle
}

internal sealed record SelectedRange(
    DateTimeOffset Start,
    DateTimeOffset End,
    string Title,
    string BreakdownTitle,
    RangeMode Mode,
    bool IsCustomStart = false);

internal sealed record UsageQueryResult(
    TokenUsageSummary Summary,
    IReadOnlyList<TokenUsageBucket> BreakdownRows,
    TimeSpan CodingTime,
    CodexQuotaEstimate? Quota,
    IReadOnlyList<CodexQuotaSnapshot> QuotaSnapshots);

internal abstract class UsageSourceModule
{
    private RangeMode mode = RangeMode.Day;

    protected UsageSourceModule(IUsageSourceReader reader)
    {
        Reader = reader;
    }

    public IUsageSourceReader Reader { get; }
    public UsageSource Source => Reader.Source;
    public string Title => Reader.Title;
    public virtual bool SupportsCycle => false;
    public DateTime PickerValue { get; set; } = DateTime.Today;
    public DateTimeOffset? CustomStartLocal { get; set; }
    public SelectedRange? LastRange { get; private set; }
    public UsageQueryResult? LastResult { get; private set; }

    public RangeMode Mode
    {
        get => mode;
        set => mode = value == RangeMode.Cycle && !SupportsCycle ? RangeMode.Day : value;
    }

    public bool TryGetDisplay(out SelectedRange range, out UsageQueryResult result)
    {
        if (LastRange is not null && LastResult is not null)
        {
            range = LastRange;
            result = LastResult;
            return true;
        }

        range = null!;
        result = null!;
        return false;
    }

    public void StoreDisplay(SelectedRange range, UsageQueryResult result)
    {
        LastRange = range;
        LastResult = result;
    }

    public void ClearDisplay()
    {
        LastRange = null;
        LastResult = null;
    }
}

internal sealed class CodexUsageModule : UsageSourceModule
{
    public CodexUsageModule()
        : base(UsageSourceReaders.For(UsageSource.Codex))
    {
    }

    public override bool SupportsCycle => true;
    public CodexQuotaEstimate? CurrentQuotaEstimate { get; set; }
    public IReadOnlyList<CodexQuotaSnapshot> CurrentQuotaSnapshots { get; set; } = Array.Empty<CodexQuotaSnapshot>();
    public IReadOnlyList<CodexQuotaCycle> QuotaCycles { get; set; } = Array.Empty<CodexQuotaCycle>();
    public CodexQuotaCycle? SelectedCycle { get; set; }
}

internal sealed class ClaudeCodeUsageModule : UsageSourceModule
{
    public ClaudeCodeUsageModule()
        : base(UsageSourceReaders.For(UsageSource.ClaudeCode))
    {
    }
}

internal sealed class ZCodeUsageModule : UsageSourceModule
{
    public ZCodeUsageModule()
        : base(UsageSourceReaders.For(UsageSource.ZCode))
    {
    }
}

internal static class UsageSourceModules
{
    public static IReadOnlyDictionary<UsageSource, UsageSourceModule> Create()
    {
        var codex = new CodexUsageModule();
        var claude = new ClaudeCodeUsageModule();
        var zcode = new ZCodeUsageModule();

        return new Dictionary<UsageSource, UsageSourceModule>
        {
            [codex.Source] = codex,
            [claude.Source] = claude,
            [zcode.Source] = zcode
        };
    }
}
