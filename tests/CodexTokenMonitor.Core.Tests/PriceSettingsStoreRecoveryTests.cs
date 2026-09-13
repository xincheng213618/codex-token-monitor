using System.Text.Json;
using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class PriceSettingsStoreRecoveryTests
{
    [Theory]
    [InlineData("{broken json")]
    [InlineData("null")]
    [InlineData("{\"Presets\":null}")]
    [InlineData("{\"CodexPresets\":[null]}")]
    public void InvalidSettingsRemainIntactAndCannotBeReplacedWithFallback(string contents)
    {
        using var isolated = new IsolatedSettings();
        File.WriteAllText(isolated.Path, contents);
        using var diagnostics = CacheOperationDiagnostics.Begin();

        var fallback = PriceSettingsStore.Load(forceReload: true);
        Assert.NotEmpty(diagnostics.Warnings);
        Assert.Throws<JsonException>(() => PriceSettingsStore.Save(fallback.Clone()));
        Assert.Equal(contents, File.ReadAllText(isolated.Path));
    }

    [Fact]
    public void CorruptionPreservesLastGoodObjectAndNextOperationRecovers()
    {
        using var isolated = new IsolatedSettings();
        PriceSettingsStore.Save(Settings("Custom pricing"));
        var good = PriceSettingsStore.Current;
        var original = File.ReadAllText(isolated.Path);
        File.WriteAllText(isolated.Path, "not json");
        using (var failed = CacheOperationDiagnostics.Begin())
        {
            Assert.Same(good, PriceSettingsStore.Current);
            Assert.NotEmpty(failed.Warnings);
            File.WriteAllText(isolated.Path, original);
            Assert.Same(good, PriceSettingsStore.Current);
            Assert.NotEmpty(failed.Warnings); // No fake recovery midway through one operation.
        }
        using var recovered = CacheOperationDiagnostics.Begin();
        Assert.Same(good, PriceSettingsStore.Current);
        Assert.Empty(recovered.Warnings);
    }

    [Fact]
    public void LockedSettingsRejectSaveAndRecoverWithoutLosingCustomValues()
    {
        using var isolated = new IsolatedSettings();
        PriceSettingsStore.Save(Settings("Saved pricing"));
        var good = PriceSettingsStore.Current;
        var original = File.ReadAllText(isolated.Path);
        using (var lockedFile = new FileStream(isolated.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        using (var failed = CacheOperationDiagnostics.Begin())
        {
            Assert.Same(good, PriceSettingsStore.Load(forceReload: true));
            Assert.ThrowsAny<IOException>(() => PriceSettingsStore.Save(Settings("Unsaved pricing")));
            Assert.NotEmpty(failed.Warnings);
        }
        Assert.Equal(original, File.ReadAllText(isolated.Path));
        using var recovered = CacheOperationDiagnostics.Begin();
        Assert.Equal("Saved pricing", PriceSettingsStore.Load(forceReload: true).GptName);
        Assert.Empty(recovered.Warnings);
        PriceSettingsStore.Save(Settings("Saved after recovery"));
        Assert.Equal("Saved after recovery", PriceSettingsStore.Current.GptName);
    }

    [Fact]
    public void EachOperationRefreshesOnceAndUnchangedSettingsReuseModelCatalogIdentity()
    {
        using var isolated = new IsolatedSettings();
        PriceSettingsStore.Save(Settings("First"));
        PriceSettings first;
        using (var operation = CacheOperationDiagnostics.Begin())
        {
            first = PriceSettingsStore.Current;
            File.WriteAllText(isolated.Path, JsonSerializer.Serialize(Settings("Second")));
            Assert.Same(first, PriceSettingsStore.Current);
            Assert.Equal("First", first.GptName);
        }
        PriceSettings second;
        using (var operation = CacheOperationDiagnostics.Begin())
        {
            second = PriceSettingsStore.Current;
            Assert.Equal("Second", second.GptName);
            Assert.NotSame(first, second);
            Assert.Empty(operation.Warnings);
        }
        using var next = CacheOperationDiagnostics.Begin();
        Assert.Same(second, PriceSettingsStore.Current);
    }

    [Fact]
    public void InterleavedOperationsKeepTheirOwnPriceSnapshot()
    {
        using var isolated = new IsolatedSettings();
        PriceSettingsStore.Save(Settings("First"));
        using var firstOperation = CacheOperationDiagnostics.Begin();
        var first = PriceSettingsStore.Current;
        using (var secondOperation = CacheOperationDiagnostics.Begin())
        {
            PriceSettingsStore.Save(Settings("Second"));
            Assert.Equal("Second", PriceSettingsStore.Current.GptName);
        }
        Assert.Same(first, PriceSettingsStore.Current);
        using var nextOperation = CacheOperationDiagnostics.Begin();
        Assert.Equal("Second", PriceSettingsStore.Current.GptName);
    }

    [Fact]
    public void UiConsumersReuseFailureSnapshotUntilAnExplicitReadOperation()
    {
        using var isolated = new IsolatedSettings();
        File.WriteAllText(isolated.Path, "broken");
        var fallback = PriceSettingsStore.Load(forceReload: true);
        File.WriteAllText(isolated.Path, JsonSerializer.Serialize(Settings("Repaired")));
        Assert.Same(fallback, PriceSettingsStore.Current);
        using var operation = CacheOperationDiagnostics.Begin();
        Assert.Equal("Repaired", PriceSettingsStore.Current.GptName);
        Assert.Empty(operation.Warnings);
    }

    [Fact]
    public void MissingFileAfterSuccessfulLoadIsReportedWithoutInventingDefaults()
    {
        using var isolated = new IsolatedSettings();
        PriceSettingsStore.Save(Settings("Known custom"));
        var good = PriceSettingsStore.Current;
        File.Move(isolated.Path, isolated.Path + ".saved");
        using var diagnostics = CacheOperationDiagnostics.Begin();
        Assert.Same(good, PriceSettingsStore.Load(forceReload: true));
        Assert.Single(diagnostics.Warnings);
        Assert.Throws<FileNotFoundException>(() => PriceSettingsStore.Save(Settings("Should not recreate")));
        Assert.False(File.Exists(isolated.Path));
    }

    [Fact]
    public void DisappearingInitiallyCorruptFileNeverBecomesSuccessfulDefaults()
    {
        using var isolated = new IsolatedSettings();
        File.WriteAllText(isolated.Path, "broken");
        PriceSettingsStore.Load(forceReload: true);
        File.Move(isolated.Path, isolated.Path + ".saved");
        for (var index = 0; index < 2; index++)
        {
            using var failed = CacheOperationDiagnostics.Begin();
            PriceSettingsStore.Load();
            Assert.NotEmpty(failed.Warnings);
            Assert.Throws<FileNotFoundException>(() => PriceSettingsStore.Save(Settings("Must not save")));
            Assert.False(File.Exists(isolated.Path));
        }
        File.WriteAllText(isolated.Path, JsonSerializer.Serialize(Settings("Repaired")));
        using var recovered = CacheOperationDiagnostics.Begin();
        Assert.Equal("Repaired", PriceSettingsStore.Current.GptName);
        Assert.Empty(recovered.Warnings);
    }

    [Fact]
    public void NewProfileCanUseDefaultsAndExplicitlySave()
    {
        using var isolated = new IsolatedSettings();
        using var diagnostics = CacheOperationDiagnostics.Begin();
        Assert.NotEmpty(PriceSettingsStore.Load(forceReload: true).CodexPresets);
        Assert.Empty(diagnostics.Warnings);
        Assert.False(File.Exists(isolated.Path));
        PriceSettingsStore.Save(Settings("New profile"));
        Assert.Equal("New profile", PriceSettingsStore.Current.GptName);
    }

    [Fact]
    public async Task ParallelRootsKeepIndependentCurrentPricesAndRecoveryState()
    {
        var firstReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Task.WhenAll(CheckRoot("first", firstReady, secondReady), CheckRoot("second", secondReady, firstReady));

        static async Task CheckRoot(string name, TaskCompletionSource ownReady, TaskCompletionSource otherReady)
        {
            using var isolated = new IsolatedSettings();
            PriceSettingsStore.Save(Settings(name));
            ownReady.TrySetResult();
            await otherReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var diagnostics = CacheOperationDiagnostics.Begin();
            Assert.Equal(name, PriceSettingsStore.Current.GptName);
            Assert.Empty(diagnostics.Warnings);
        }
    }

    private static PriceSettings Settings(string name)
    {
        var settings = PriceSettingsStore.Defaults();
        settings.GptName = name;
        settings.GptUncachedInputPerMillion = 123m;
        return settings;
    }

    private sealed class IsolatedSettings : IDisposable
    {
        private readonly IDisposable cacheScope;
        private readonly IDisposable logScope;
        public string Path { get; }

        public IsolatedSettings()
        {
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PriceRecovery-" + Guid.NewGuid().ToString("N"));
            cacheScope = MonitorCachePaths.PushLocalAppDataRoot(root);
            logScope = UsageLogPaths.PushRoot(System.IO.Path.Combine(root, "logs"));
            var directory = System.IO.Path.Combine(root, "CodexTokenMonitor");
            Directory.CreateDirectory(directory);
            Path = System.IO.Path.Combine(directory, "price-settings.json");
        }

        public void Dispose()
        {
            logScope.Dispose();
            cacheScope.Dispose();
        }
    }
}
