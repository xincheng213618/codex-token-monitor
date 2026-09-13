using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class AnalysisQuerySessionTests
{
    [Fact]
    public async Task ParentStopCancelsAndDrainsRegisteredAnalysis()
    {
        using var runtime = new MonitorRuntime();
        using var session = new AnalysisQuerySession(runtime);
        var started = Signal();
        var blocked = Signal();
        var query = session.RunAsync("cycle analysis", token =>
        {
            started.SetResult();
            blocked.Task.Wait(token);
            return 42;
        }, requiresSharedIo: true);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var shutdown = await runtime.StopAsync(TimeSpan.FromSeconds(5));
        Assert.True(shutdown.Completed);
        Assert.True(session.IsStopping);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => query);
        Assert.Empty(runtime.Failures);
    }

    [Fact]
    public async Task ClosingOneSessionDoesNotStopParentOrSibling()
    {
        using var runtime = new MonitorRuntime();
        using var firstSession = new AnalysisQuerySession(runtime);
        using var secondSession = new AnalysisQuerySession(runtime);
        var firstStarted = Signal();
        var secondStarted = Signal();
        var release = Signal();
        var first = firstSession.RunAsync("first window", token =>
        {
            firstStarted.SetResult();
            release.Task.Wait(token);
            return 1;
        });
        var second = secondSession.RunAsync("second window", token =>
        {
            secondStarted.SetResult();
            release.Task.Wait(token);
            return 2;
        });

        try
        {
            await Task.WhenAll(firstStarted.Task, secondStarted.Task).WaitAsync(TimeSpan.FromSeconds(5));
            firstSession.Dispose();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            Assert.False(runtime.IsStopping);
            Assert.False(secondSession.IsStopping);
            Assert.False(second.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
        }

        Assert.Equal(2, (await second).Value);
    }

    [Fact]
    public async Task ClosedSessionAndStoppedParentRejectNewQueries()
    {
        using var runtime = new MonitorRuntime();
        using var closed = new AnalysisQuerySession(runtime);
        using var parentStopped = new AnalysisQuerySession(runtime);
        var calls = 0;
        closed.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => closed.RunAsync("closed", _ => ++calls));
        Assert.False(runtime.IsStopping);

        runtime.BeginStop();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => parentStopped.RunAsync("parent stopped", _ => ++calls));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ClosingWhileWaitingForSharedGateNeverStartsQuery()
    {
        using var runtime = new MonitorRuntime();
        using var session = new AnalysisQuerySession(runtime);
        await runtime.SharedIoGate.WaitAsync();
        try
        {
            var called = false;
            var query = session.RunAsync("waiting analysis", _ =>
            {
                called = true;
                return 42;
            }, requiresSharedIo: true);
            Assert.False(query.IsCompleted);

            session.Dispose();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => query.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(called);
            Assert.False(runtime.IsStopping);
            Assert.Empty(runtime.Failures);
        }
        finally
        {
            runtime.SharedIoGate.Release();
        }
    }

    [Fact]
    public async Task ReadOnlyQueriesRunConcurrentlyWhileSharedGateIsOccupied()
    {
        using var runtime = new MonitorRuntime();
        using var session = new AnalysisQuerySession(runtime);
        await runtime.SharedIoGate.WaitAsync();
        var release = Signal();
        var firstStarted = Signal();
        var secondStarted = Signal();
        var first = session.RunAsync("cached curve", token =>
        {
            firstStarted.SetResult();
            release.Task.Wait(token);
            return 1;
        });
        var second = session.RunAsync("cached estimate", token =>
        {
            secondStarted.SetResult();
            release.Task.Wait(token);
            return 2;
        });

        try
        {
            await Task.WhenAll(firstStarted.Task, secondStarted.Task).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            runtime.SharedIoGate.Release();
        }

        Assert.Equal(1, (await first).Value);
        Assert.Equal(2, (await second).Value);
    }

    [Fact]
    public async Task QueryReturnsIsolatedCacheWarningsAlongsideValue()
    {
        using var session = new AnalysisQuerySession();
        using var outerDiagnostics = CacheOperationDiagnostics.Begin();
        var result = await session.RunAsync("cached estimate", _ =>
        {
            CacheOperationDiagnostics.Report("test-cache.db", "ReadRange", new IOException("cache unavailable"));
            return 42;
        });

        Assert.Equal(42, result.Value);
        var warning = Assert.Single(result.CacheWarnings);
        Assert.Equal("ReadRange", warning.Operation);
        Assert.Equal("cache unavailable", warning.Message);
        Assert.Empty(outerDiagnostics.Warnings);
        Assert.Empty(session.Runtime.Failures);
    }

    [Fact]
    public async Task RequestCancellationDoesNotStopWindowOrParent()
    {
        using var runtime = new MonitorRuntime();
        using var session = new AnalysisQuerySession(runtime);
        using var request = new CancellationTokenSource();
        var started = Signal();
        var blocked = Signal();
        var query = session.RunAsync("manual estimate", token =>
        {
            started.SetResult();
            blocked.Task.Wait(token);
            return 1;
        }, cancellationToken: request.Token);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        request.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => query);
        Assert.False(session.IsStopping);
        Assert.False(runtime.IsStopping);
        Assert.Equal(2, (await session.RunAsync("retry", _ => 2)).Value);
    }

    [Fact]
    public async Task LateQueryFailureRemainsObservableAfterParentShutdownTimeout()
    {
        using var runtime = new MonitorRuntime();
        using var session = new AnalysisQuerySession(runtime);
        var started = Signal();
        var release = Signal();
        var expected = new InvalidOperationException("analysis failed late");
        var query = session.RunAsync<int>("slow analysis", _ =>
        {
            started.SetResult();
            release.Task.GetAwaiter().GetResult();
            throw expected;
        });

        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var shutdown = await runtime.StopAsync(TimeSpan.Zero);
            Assert.False(shutdown.Completed);
            Assert.Contains("slow analysis", shutdown.PendingOperations);
            session.Dispose();
        }
        finally
        {
            release.TrySetResult();
        }

        Assert.Same(expected, await Assert.ThrowsAsync<InvalidOperationException>(() => query));
        Assert.Same(expected, Assert.Single(runtime.Failures).Exception);
    }

    [Fact]
    public async Task StandaloneSessionCancelsLateSuccessAndKeepsGateUntilQueryExits()
    {
        using var session = new AnalysisQuerySession();
        var runtime = session.Runtime;
        var started = Signal();
        var release = Signal();
        var cancellationEntered = Signal();
        using var cancellationRelease = new ManualResetEventSlim();
        CancellationToken queryToken = default;
        CancellationTokenRegistration blockingRegistration = default;
        var query = session.RunAsync("standalone analysis", token =>
        {
            queryToken = token;
            started.SetResult();
            release.Task.GetAwaiter().GetResult();
            return 42;
        }, requiresSharedIo: true);

        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // Registrations run in reverse order. Register after the query has
            // linked its token so this callback holds back linked cancellation.
            blockingRegistration = runtime.LifetimeToken.Register(() =>
            {
                cancellationEntered.TrySetResult();
                cancellationRelease.Wait();
            });
            session.Dispose();
            await cancellationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(runtime.IsStopping);
            Assert.True(runtime.LifetimeToken.IsCancellationRequested);
            Assert.False(queryToken.IsCancellationRequested);
            Assert.False(query.IsCompleted);
            Assert.Equal(0, runtime.SharedIoGate.CurrentCount);
            release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => query.WaitAsync(TimeSpan.FromSeconds(5)));
            // The query released its gate, but the blocked cancellation callback
            // still keeps runtime resources alive until it too has finished.
            Assert.Equal(1, runtime.SharedIoGate.CurrentCount);
        }
        finally
        {
            release.TrySetResult();
            cancellationRelease.Set();
            blockingRegistration.Dispose();
        }

        Assert.Empty(runtime.Failures);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParentAndRequestCancellationRejectLateSuccessBeforeLinkedCallbacksRun(bool cancelParent)
    {
        using var runtime = new MonitorRuntime();
        using var session = new AnalysisQuerySession(runtime);
        using var request = new CancellationTokenSource();
        using var cancellationRelease = new ManualResetEventSlim();
        var started = Signal();
        var release = Signal();
        var cancellationEntered = Signal();
        var sourceToken = cancelParent ? runtime.LifetimeToken : request.Token;
        CancellationToken queryToken = default;
        CancellationTokenRegistration blockingRegistration = default;
        Task? requestCancellation = null;
        var query = session.RunAsync("late parent/request analysis", token =>
        {
            queryToken = token;
            started.TrySetResult();
            release.Task.GetAwaiter().GetResult();
            return 42;
        }, requiresSharedIo: true, cancellationToken: request.Token);

        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            blockingRegistration = sourceToken.Register(() =>
            {
                cancellationEntered.TrySetResult();
                cancellationRelease.Wait();
            });
            if (cancelParent) runtime.BeginStop();
            else requestCancellation = request.CancelAsync();
            await cancellationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(sourceToken.IsCancellationRequested);
            Assert.False(queryToken.IsCancellationRequested);
            Assert.Equal(cancelParent, session.IsStopping);
            release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => query.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(1, runtime.SharedIoGate.CurrentCount);
            Assert.Empty(runtime.Failures);
        }
        finally
        {
            release.TrySetResult();
            cancellationRelease.Set();
            blockingRegistration.Dispose();
            if (requestCancellation is not null) await requestCancellation;
        }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
