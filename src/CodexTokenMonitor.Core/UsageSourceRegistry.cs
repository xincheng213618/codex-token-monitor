namespace CodexTokenMonitor;

/// <summary>
/// Source metadata is shared and readers are singletons. Desktop page state and
/// factories live in WPF. Registration never reads logs, caches, or price settings.
/// </summary>
internal sealed class UsageSourceDefinition
{
    private readonly Lazy<IUsageSourceReader> reader;

    public UsageSourceDefinition(
        UsageSource source,
        string title,
        string priceGroup,
        bool supportsQuota,
        Func<IUsageSourceReader> createReader,
        params string[] priceGroupAliases)
    {
        Source = source;
        Title = title;
        PriceGroup = priceGroup;
        SupportsQuota = supportsQuota;
        reader = new Lazy<IUsageSourceReader>(createReader);
        PriceGroupAliases = Array.AsReadOnly(priceGroupAliases.ToArray());
    }

    public UsageSource Source { get; }
    public string Title { get; }
    public string PriceGroup { get; }
    public bool SupportsQuota { get; }
    public IReadOnlyList<string> PriceGroupAliases { get; }
    public IUsageSourceReader Reader => reader.Value;
    public IUsageCacheQuery CachedQueries => Reader;
    public IUsageQuery Queries => Reader;
    public IUsageCacheMaintenance CacheMaintenance => Reader;

}

internal static class UsageSourceRegistry
{
    // This explicit order is also the existing main/price tab order. Keep it
    // separate from persisted enum values when adding a source in the future.
    // Reader factories remain deferred; metadata access performs no I/O.
    public static IReadOnlyList<UsageSourceDefinition> All { get; } = Array.AsReadOnly(new[]
    {
        new UsageSourceDefinition(UsageSource.Codex, "Codex", PricePresetGroups.Codex, true,
            UsageSourceReaders.CreateCodexReader),
        new UsageSourceDefinition(UsageSource.ClaudeCode, "Claude Code", PricePresetGroups.ClaudeCode, false,
            UsageSourceReaders.CreateClaudeCodeReader,
            "claude", "claudecode"),
        new UsageSourceDefinition(UsageSource.ZCode, "ZCode", PricePresetGroups.ZCode, false,
            UsageSourceReaders.CreateZCodeReader,
            "glm", "z.ai", "zai", "智谱"),
        new UsageSourceDefinition(UsageSource.WorkBuddy, "WorkBuddy", PricePresetGroups.WorkBuddy, false,
            UsageSourceReaders.CreateWorkBuddyReader,
            "work buddy", "buddy"),
        new UsageSourceDefinition(UsageSource.Dsh, "DSH", PricePresetGroups.Dsh, false,
            UsageSourceReaders.CreateDshReader,
            "deepseek harness", "deepseek-harness", "harness"),
        new UsageSourceDefinition(UsageSource.Kimi, "Kimi", PricePresetGroups.Kimi, false,
            UsageSourceReaders.CreateKimiReader,
            "kimi code", "kimi work")
    });

    private static readonly IReadOnlyDictionary<UsageSource, UsageSourceDefinition> BySource =
        All.ToDictionary(definition => definition.Source);
    private static readonly IReadOnlyDictionary<string, UsageSourceDefinition> ByPriceGroup = All
        .SelectMany(definition => definition.PriceGroupAliases.Prepend(definition.PriceGroup)
            .Select(alias => (Alias: alias.Trim().ToLowerInvariant(), Definition: definition)))
        .ToDictionary(entry => entry.Alias, entry => entry.Definition, StringComparer.Ordinal);
    private static readonly Lazy<IReadOnlyList<IUsageSourceReader>> SharedReaders = new(() =>
        Array.AsReadOnly(All.Select(definition => definition.Reader).ToArray()));

    public static UsageSourceDefinition Default => All[0];
    public static IReadOnlyList<IUsageSourceReader> Readers => SharedReaders.Value;
    public static IReadOnlyList<string> PriceGroups { get; } =
        Array.AsReadOnly(All.Select(definition => definition.PriceGroup).ToArray());

    public static UsageSourceDefinition For(UsageSource source) =>
        BySource.TryGetValue(source, out var definition) ? definition : Default;

    public static UsageSourceDefinition ForPriceGroup(string group) =>
        ByPriceGroup.TryGetValue(group.Trim().ToLowerInvariant(), out var definition) ? definition : Default;

    public static int IndexOf(UsageSource source)
    {
        var definition = For(source);
        for (var index = 0; index < All.Count; index++)
            if (ReferenceEquals(All[index], definition)) return index;
        return 0;
    }
}
