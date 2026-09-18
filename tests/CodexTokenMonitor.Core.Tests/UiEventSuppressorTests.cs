using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class UiEventSuppressorTests
{
    [Fact]
    public void Begin_SuppressesUntilScopeIsDisposed()
    {
        var suppressor = new UiEventSuppressor();
        Assert.False(suppressor.IsSuppressing);

        using var scope = suppressor.Begin();
        Assert.True(suppressor.IsSuppressing);

        scope.Dispose();
        Assert.False(suppressor.IsSuppressing);
    }

    [Fact]
    public void NestedScopes_ReleaseOnlyAfterOutermostScopeEnds()
    {
        var suppressor = new UiEventSuppressor();
        using var outer = suppressor.Begin();
        using var inner = suppressor.Begin();

        inner.Dispose();
        Assert.True(suppressor.IsSuppressing);

        outer.Dispose();
        Assert.False(suppressor.IsSuppressing);
    }

    [Fact]
    public void ScopeDisposedTwice_DecrementsDepthOnlyOnce()
    {
        var suppressor = new UiEventSuppressor();
        var scope = suppressor.Begin();

        scope.Dispose();
        scope.Dispose();

        Assert.False(suppressor.IsSuppressing);
    }

    [Fact]
    public void ScopeRelease_RunsWhenGuardedBlockThrows()
    {
        var suppressor = new UiEventSuppressor();
        try
        {
            using var scope = suppressor.Begin();
            Assert.True(suppressor.IsSuppressing);
            throw new InvalidOperationException("programmatic update failed");
        }
        catch (InvalidOperationException)
        {
        }

        Assert.False(suppressor.IsSuppressing);
    }

    [Fact]
    public void OrphanScopeDispose_CannotUnmaskLaterSuppressions()
    {
        var suppressor = new UiEventSuppressor();
        var orphaned = suppressor.Begin();

        using (suppressor.Begin())
        {
            // A scope whose using block was left earlier must not decrement the
            // depth that a newer scope is holding.
            orphaned.Dispose();
            Assert.True(suppressor.IsSuppressing);
        }

        Assert.False(suppressor.IsSuppressing);
    }

    [Fact]
    public async Task ParallelScopes_CountIsThreadSafe()
    {
        var suppressor = new UiEventSuppressor();
        const int parallelScopes = 64;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var tasks = Enumerable.Range(0, parallelScopes).Select(_ => Task.Run(async () =>
        {
            using var scope = suppressor.Begin();
            await started.Task;
        })).ToArray();

        // Every scope is open while the tasks wait; release them together.
        started.SetResult();
        await Task.WhenAll(tasks);

        Assert.False(suppressor.IsSuppressing);
    }
}
