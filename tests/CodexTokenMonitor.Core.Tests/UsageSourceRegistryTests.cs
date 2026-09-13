using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class UsageSourceRegistryTests
{
    private static readonly UsageSource[] Sources =
    {
        UsageSource.Codex, UsageSource.ClaudeCode, UsageSource.ZCode, UsageSource.WorkBuddy, UsageSource.Dsh
    };

    [Fact]
    public void Registry_PreservesPersistedIdsTitlesAndDisplayOrder()
    {
        var titles = new[] { "Codex", "Claude Code", "ZCode", "WorkBuddy", "DSH" };
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, Sources.Select(source => (int)source));
        Assert.Equal(Sources, Enum.GetValues<UsageSource>());
        Assert.Equal(Sources, UsageSourceRegistry.All.Select(definition => definition.Source));
        Assert.Equal(titles, UsageSourceRegistry.All.Select(definition => definition.Title));
        Assert.Equal(titles, PricePresetGroups.All);
        Assert.Equal(Sources, UsageSourceReaders.All.Select(reader => reader.Source));

        for (var index = 0; index < Sources.Length; index++)
        {
            var source = Sources[index];
            var definition = UsageSourceRegistry.For(source);
            var reader = UsageSourceReaders.For(source);
            Assert.Equal(index, UsageSourceRegistry.IndexOf(source));
            Assert.Same(definition, UsageSourceRegistry.ForPriceGroup(titles[index]));
            Assert.Equal(titles[index], PricePresetGroups.ForSource(source));
            Assert.Equal(titles[index], reader.Title);
            Assert.Equal(source == UsageSource.Codex, reader.SupportsQuota);
            Assert.Same(reader, definition.Reader);
            Assert.Same(reader, UsageSourceReaders.All[index]);
            Assert.Same(reader, UsageSourceReaders.For(source));
        }
    }

    [Theory]
    [InlineData("claude", "Claude Code")]
    [InlineData("claudecode", "Claude Code")]
    [InlineData("  ClAuDe CoDe  ", "Claude Code")]
    [InlineData("glm", "ZCode")]
    [InlineData("z.ai", "ZCode")]
    [InlineData("zai", "ZCode")]
    [InlineData("智谱", "ZCode")]
    [InlineData("work buddy", "WorkBuddy")]
    [InlineData("buddy", "WorkBuddy")]
    [InlineData("deepseek harness", "DSH")]
    [InlineData("deepseek-harness", "DSH")]
    [InlineData("harness", "DSH")]
    [InlineData("", "Codex")]
    [InlineData("unknown provider", "Codex")]
    public void PriceGroups_PreserveLegacyAliasesAndUnknownFallback(string input, string expected)
    {
        Assert.Equal(expected, PricePresetGroups.Normalize(input));
        Assert.Equal(expected, UsageSourceRegistry.ForPriceGroup(input).PriceGroup);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(999)]
    public void UnknownSource_PreservesCodexFallbackAcrossConsumers(int value)
    {
        var source = (UsageSource)value;
        Assert.Same(UsageSourceRegistry.For(UsageSource.Codex), UsageSourceRegistry.For(source));
        Assert.Same(UsageSourceReaders.For(UsageSource.Codex), UsageSourceReaders.For(source));
        Assert.Equal(PricePresetGroups.Codex, PricePresetGroups.ForSource(source));
        Assert.Equal(0, UsageSourceRegistry.IndexOf(source));
    }

    [Fact]
    public void Modules_KeepConcreteTypesAndPrivateWindowState()
    {
        var firstWindow = UsageSourceModules.Create();
        var secondWindow = UsageSourceModules.Create();
        var types = new[]
        {
            typeof(CodexUsageModule), typeof(ClaudeCodeUsageModule), typeof(ZCodeUsageModule),
            typeof(WorkBuddyUsageModule), typeof(DshUsageModule)
        };
        Assert.Equal(Sources, firstWindow.Keys);
        Assert.Equal(types, firstWindow.Values.Select(module => module.GetType()));
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(8));
        var range = new SelectedRange(start, start.AddDays(1), "day", "details", RangeMode.Day);
        var result = new UsageQueryResult(new TokenUsageSummary { TotalTokens = 42 },
            Array.Empty<TokenUsageBucket>(), TimeSpan.Zero, null, Array.Empty<CodexQuotaSnapshot>());

        foreach (var source in Sources)
        {
            var first = firstWindow[source];
            var second = secondWindow[source];
            Assert.NotSame(first, second);
            Assert.Equal(source, first.Source);
            Assert.Equal(first.Title, second.Title);
            Assert.Equal(UsageSourceRegistry.For(source).SupportsQuota, first.SupportsQuota);
            Assert.Equal(source == UsageSource.Codex, first.SupportsCycle);
            first.Mode = RangeMode.Cycle;
            Assert.Equal(source == UsageSource.Codex ? RangeMode.Cycle : RangeMode.Day, first.Mode);
            Assert.Equal(RangeMode.Day, second.Mode);
            first.CustomStartLocal = start;
            first.StoreDisplay(range, result);
            first.CacheDisplay(range, result);
            Assert.Null(second.CustomStartLocal);
            Assert.False(second.TryGetDisplay(out _, out _));
            Assert.False(second.TryGetCachedDisplay(range, out _));
        }

        var firstCodex = Assert.IsType<CodexUsageModule>(firstWindow[UsageSource.Codex]);
        var secondCodex = Assert.IsType<CodexUsageModule>(secondWindow[UsageSource.Codex]);
        var cycle = new CodexQuotaCycle(start, start.AddDays(7), start.AddDays(7), 1, 10, true);
        firstCodex.QuotaCycles = new[] { cycle };
        firstCodex.SelectedCycle = cycle;
        Assert.Empty(secondCodex.QuotaCycles);
        Assert.Null(secondCodex.SelectedCycle);
    }

    [Fact]
    public void RegistryAndFactories_DoNotTouchSettingsCachesOrSourceLogs()
    {
        var root = Path.Combine(Path.GetTempPath(), $"UsageSourceRegistryTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var cacheRoot = MonitorCachePaths.PushLocalAppDataRoot(root);
            using var logRoot = UsageLogPaths.PushRoot(Path.Combine(root, "logs"));
            using var diagnostics = CacheOperationDiagnostics.Begin();

            _ = PricePresetGroups.All;
            _ = UsageSourceReaders.All;
            var modules = UsageSourceModules.Create();
            foreach (var definition in UsageSourceRegistry.All)
            {
                Assert.Equal(definition.Source, definition.Reader.Source);
                Assert.Equal(definition.Title, modules[definition.Source].Title);
            }

            Assert.Empty(diagnostics.Warnings);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories));
        }
        finally
        {
            var resolved = Path.GetFullPath(root);
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(temp, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolved).StartsWith("UsageSourceRegistryTests-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing to clean up outside the isolated test directory.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        }
    }
}
