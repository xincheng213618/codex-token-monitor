using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class MonitorRuntimeTests
{
    [Fact]
    public async Task RunReturnsGenericResultAndStartsOnCallingContext()
    {
        using var runtime = new MonitorRuntime();
        var context = new NonPumpingSynchronizationContext();
        var token = runtime.LifetimeToken;
        var result = context.Invoke(() => runtime.Run("result", cancellation =>
        {
            Assert.Same(context, SynchronizationContext.Current);
            Assert.Equal(token, cancellation);
            return Task.FromResult(42);
        }));

        Assert.Equal(42, await result);
        var stopped = await runtime.StopAsync(TimeSpan.FromSeconds(5));
        Assert.True(stopped.Completed);
        Assert.Empty(stopped.PendingOperations);
        Assert.Empty(stopped.Failures);
    }

    [Fact]
    public async Task BeginStopRejectsNewCallbacksIncludingReentrantRequests()
    {
        using var runtime = new MonitorRuntime();
        var lateWasInvoked = false;
        Task? rejected = null;
        var first = runtime.Run("first", _ =>
        {
            runtime.BeginStop();
            rejected = runtime.Run("late", _ =>
            {
                lateWasInvoked = true;
                return Task.CompletedTask;
            });
            return Task.CompletedTask;
        });

        await first;
        Assert.True(runtime.IsStopping);
        Assert.True(runtime.LifetimeToken.IsCancellationRequested);
        Assert.NotNull(rejected);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rejected!);
        Assert.False(lateWasInvoked);
        Assert.True((await runtime.StopAsync(TimeSpan.FromSeconds(5))).Completed);
    }

    [Fact]
    public async Task StopCancelsAndDrainsAllRegisteredOperations()
    {
        using var runtime = new MonitorRuntime();
        var blocked = Signal();
        var work = Enumerable.Range(1, 3)
            .Select(index => runtime.Run($"work {index}", token => blocked.Task.WaitAsync(token)))
            .ToArray();

        var shutdown = runtime.StopAsync(TimeSpan.FromSeconds(5));
        Assert.Same(shutdown, runtime.StopAsync(TimeSpan.Zero));
        var result = await shutdown;
        Assert.True(result.Completed);
        Assert.Empty(result.PendingOperations);
        Assert.Empty(result.Failures);
        foreach (var task in work)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
            Assert.True(task.IsCanceled);
        }
    }

    [Fact]
    public async Task SynchronousFailureIsObservableThroughTaskAndFailureSnapshot()
    {
        using var runtime = new MonitorRuntime();
        var expected = new InvalidOperationException("reader failed");
        var task = runtime.Run<int>("read usage", _ => throw expected);

        Assert.Same(expected, await Assert.ThrowsAsync<InvalidOperationException>(() => task));
        var failure = Assert.Single(runtime.Failures);
        Assert.Equal("read usage", failure.OperationName);
        Assert.Same(expected, failure.Exception);
        var stopped = await runtime.StopAsync(TimeSpan.FromSeconds(5));
        Assert.True(stopped.Completed);
        Assert.Same(expected, Assert.Single(stopped.Failures).Exception);
    }

    [Fact]
    public async Task TimeoutReportsAllPendingWorkAndLateFailuresRemainObservable()
    {
        using var runtime = new MonitorRuntime();
        var release = Signal();
        var expected = new InvalidOperationException("late failure");
        var lateFailure = runtime.Run("late fault", async _ =>
        {
            await release.Task;
            throw expected;
        });
        var lateSuccess = runtime.Run("late success", _ => release.Task);

        var shutdown = runtime.StopAsync(TimeSpan.Zero);
        var stopped = await shutdown;
        Assert.False(stopped.Completed);
        Assert.Contains("late fault", stopped.PendingOperations);
        Assert.Contains("late success", stopped.PendingOperations);
        Assert.Empty(stopped.Failures);
        Assert.Same(shutdown, runtime.StopAsync(TimeSpan.FromSeconds(5)));

        release.SetResult();
        await lateSuccess;
        Assert.Same(expected, await Assert.ThrowsAsync<InvalidOperationException>(() => lateFailure));
        Assert.Same(expected, Assert.Single(runtime.Failures).Exception);
        // The original shutdown result describes the timeout moment; the runtime
        // snapshot remains available for failures arriving after that moment.
        Assert.Empty(stopped.Failures);
    }

    [Fact]
    public async Task DisposeAfterTimeoutDoesNotDisposeGateWhileOperationStillHoldsIt()
    {
        var runtime = new MonitorRuntime();
        var token = runtime.LifetimeToken;
        var release = Signal();
        var gateEntered = Signal();
        var operation = runtime.Run("gate owner", async cancellation =>
        {
            await runtime.SharedIoGate.WaitAsync(cancellation);
            gateEntered.SetResult();
            try
            {
                await release.Task;
            }
            finally
            {
                runtime.SharedIoGate.Release();
            }
        });

        await gateEntered.Task;
        try
        {
            Assert.False((await runtime.StopAsync(TimeSpan.Zero)).Completed);
            runtime.Dispose();
            Assert.False(await runtime.SharedIoGate.WaitAsync(0));
            Assert.Equal(token, runtime.LifetimeToken);
            Assert.True(runtime.LifetimeToken.IsCancellationRequested);
        }
        finally
        {
            release.TrySetResult();
        }

        // In particular, the operation's finally can release the still-live gate.
        await operation;
        Assert.Empty(runtime.Failures);
        runtime.Dispose();
    }

    [Fact]
    public async Task ThrowingCancellationCallbackIsRecordedAndDoesNotPreventDrain()
    {
        using var runtime = new MonitorRuntime();
        var expected = new InvalidOperationException("cancellation callback failed");
        var anotherCallbackRan = false;
        using var firstRegistration = runtime.LifetimeToken.Register(() => anotherCallbackRan = true);
        using var secondRegistration = runtime.LifetimeToken.Register(() => throw expected);

        var result = await runtime.StopAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.Completed);
        Assert.True(anotherCallbackRan);
        var failure = Assert.Single(result.Failures);
        Assert.Equal("Lifetime cancellation", failure.OperationName);
        Assert.Contains(expected, Assert.IsType<AggregateException>(failure.Exception).Flatten().InnerExceptions);
    }

    [Fact]
    public async Task CancellationCallbacksAreIncludedInBudgetWithoutBlockingCaller()
    {
        using var runtime = new MonitorRuntime();
        using var release = new ManualResetEventSlim();
        var entered = Signal();
        using var registration = runtime.LifetimeToken.Register(() =>
        {
            entered.SetResult();
            release.Wait();
        });

        try
        {
            var result = await runtime.StopAsync(TimeSpan.Zero);
            Assert.False(result.Completed);
            Assert.Contains("Lifetime cancellation", result.PendingOperations);
            Assert.True(runtime.LifetimeToken.IsCancellationRequested);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task RuntimeBookkeepingAndShutdownDoNotRequireCallerContextToPump()
    {
        using var runtime = new MonitorRuntime();
        var context = new NonPumpingSynchronizationContext();
        var release = Signal();
        var operation = context.Invoke(() => runtime.Run("background result", _ => release.Task));
        var shutdown = context.Invoke(() => runtime.StopAsync(TimeSpan.FromSeconds(5)));

        release.SetResult();
        await operation;
        Assert.True((await shutdown).Completed);
        Assert.Equal(0, context.PostCount);
    }

    [Fact]
    public async Task FailureHistoryKeepsLatest128WithoutChangingReturnedExceptions()
    {
        using var runtime = new MonitorRuntime();
        for (var index = 0; index < 140; index++)
        {
            var expected = new InvalidOperationException($"failure {index}");
            var operation = runtime.Run($"operation {index}", _ => Task.FromException(expected));
            Assert.Same(expected, await Assert.ThrowsAsync<InvalidOperationException>(() => operation));
        }

        Assert.Equal(128, runtime.Failures.Count);
        Assert.Equal("operation 12", runtime.Failures[0].OperationName);
        Assert.Equal("operation 139", runtime.Failures[^1].OperationName);
        var stopped = await runtime.StopAsync(TimeSpan.FromSeconds(5));
        Assert.True(stopped.Completed);
        Assert.Equal(128, stopped.Failures.Count);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class NonPumpingSynchronizationContext : SynchronizationContext
    {
        private int postCount;
        public int PostCount => Volatile.Read(ref postCount);
        public override void Post(SendOrPostCallback callback, object? state) => Interlocked.Increment(ref postCount);

        public T Invoke<T>(Func<T> action)
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try { return action(); }
            finally { SetSynchronizationContext(previous); }
        }
    }
}
