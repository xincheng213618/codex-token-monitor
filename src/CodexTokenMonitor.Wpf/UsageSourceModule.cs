namespace CodexTokenMonitor;

/// <summary>
/// Per-window page selection and accepted display cache, owned by the UI thread.
/// It holds source metadata only; query and maintenance capabilities are selected
/// by the window when it captures a request.
/// </summary>
internal abstract class UsageSourceModule
{
    private const int DisplayCacheCapacity = 8;

    private readonly Dictionary<DisplayCacheKey, UsageQueryResult> displayCache = new();
    private readonly List<DisplayCacheKey> displayCacheOrder = new();
    private RangeMode mode = RangeMode.Day;

    protected UsageSourceModule(UsageSource source)
    {
        var definition = UsageSourceRegistry.For(source);
        Source = definition.Source;
        Title = definition.Title;
        SupportsQuota = definition.SupportsQuota;
    }

    public UsageSource Source { get; }
    public string Title { get; }
    public bool SupportsQuota { get; }
    public virtual bool SupportsCycle => false;
    public DateTime PickerValue { get; set; } = BeijingClock.Today;
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
        if (result.CacheWarnings.Count > 0) return;
        var displayResult = WithoutDetailRows(result);
        LastRange = range;
        LastResult = displayResult;

        var key = DisplayCacheKey.From(range);
        if (displayCache.ContainsKey(key))
        {
            displayCache[key] = displayResult;
            TouchCachedDisplay(key);
        }
    }

    public bool TryGetCachedDisplay(SelectedRange range, out UsageQueryResult result)
    {
        var key = DisplayCacheKey.From(range);
        if (displayCache.TryGetValue(key, out result!))
        {
            TouchCachedDisplay(key);
            return true;
        }

        result = null!;
        return false;
    }

    public void CacheDisplay(SelectedRange range, UsageQueryResult result)
    {
        if (result.CacheWarnings.Count > 0) return;
        var key = DisplayCacheKey.From(range);
        displayCache[key] = WithoutDetailRows(result);
        TouchCachedDisplay(key);

        while (displayCacheOrder.Count > DisplayCacheCapacity)
        {
            var oldest = displayCacheOrder[0];
            displayCacheOrder.RemoveAt(0);
            displayCache.Remove(oldest);
        }
    }

    public void ClearDisplay()
    {
        LastRange = null;
        LastResult = null;
        displayCache.Clear();
        displayCacheOrder.Clear();
    }

    private void TouchCachedDisplay(DisplayCacheKey key)
    {
        displayCacheOrder.Remove(key);
        displayCacheOrder.Add(key);
    }

    private static UsageQueryResult WithoutDetailRows(UsageQueryResult result)
    {
        return result.DetailRows.Count == 0
            ? result
            : result with { DetailRows = Array.Empty<TokenUsageBucket>() };
    }

    private readonly record struct DisplayCacheKey(
        DateTimeOffset Start,
        DateTimeOffset End,
        RangeMode Mode,
        bool IsCustomStart)
    {
        public static DisplayCacheKey From(SelectedRange range)
        {
            return new DisplayCacheKey(range.Start, range.End, range.Mode, range.IsCustomStart);
        }
    }
}

internal sealed class CodexUsageModule : UsageSourceModule
{
    public CodexUsageModule()
        : base(UsageSource.Codex)
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
        : base(UsageSource.ClaudeCode)
    {
    }
}

internal sealed class ZCodeUsageModule : UsageSourceModule
{
    public ZCodeUsageModule()
        : base(UsageSource.ZCode)
    {
    }
}

internal sealed class WorkBuddyUsageModule : UsageSourceModule
{
    public WorkBuddyUsageModule()
        : base(UsageSource.WorkBuddy)
    {
    }
}

internal sealed class DshUsageModule : UsageSourceModule
{
    public DshUsageModule()
        : base(UsageSource.Dsh)
    {
    }
}

internal static class UsageSourceModules
{
    public static IReadOnlyDictionary<UsageSource, UsageSourceModule> Create()
    {
        return UsageSourceRegistry.All.ToDictionary(definition => definition.Source, definition => CreateModule(definition.Source));
    }

    // Source order and metadata come from Core. The desktop owns its concrete
    // page factories and creates new mutable state for every window.
    private static UsageSourceModule CreateModule(UsageSource source) => source switch
    {
        UsageSource.Codex => new CodexUsageModule(),
        UsageSource.ClaudeCode => new ClaudeCodeUsageModule(),
        UsageSource.ZCode => new ZCodeUsageModule(),
        UsageSource.WorkBuddy => new WorkBuddyUsageModule(),
        UsageSource.Dsh => new DshUsageModule(),
        _ => throw new InvalidOperationException($"No desktop page registered for source {source}.")
    };
}
