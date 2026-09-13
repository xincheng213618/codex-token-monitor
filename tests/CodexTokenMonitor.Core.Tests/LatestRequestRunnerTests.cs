using System.Collections.Concurrent;
using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class LatestRequestRunnerTests
{
    [Fact]
    public async Task BurstKeepsOnlyLatestPendingRequestAndMarksRunningResultStale()
    {
        var firstRelease = Signal();
        var latestStarted = Signal();
        var latestRelease = Signal();
        var calls = new List<int>();
        var versions = new List<long>();
        var activeCount = 0;
        var maximumActiveCount = 0;
        var runner = new LatestRequestRunner<int>(async (request, version, _) =>
        {
            activeCount++;
            maximumActiveCount = Math.Max(maximumActiveCount, activeCount);
            calls.Add(request);
            versions.Add(version);
            if (request == 1)
                await firstRelease.Task;
            else
            {
                latestStarted.SetResult();
                await latestRelease.Task;
            }
            activeCount--;
        });

        var run = runner.RequestAsync(1);
        Assert.True(runner.IsRunning);
        Assert.True(runner.IsCurrent(1));
        Assert.Same(run, runner.RequestAsync(2));
        Assert.Same(run, runner.RequestAsync(3));
        Assert.Same(run, runner.Completion);
        Assert.False(runner.IsCurrent(1));
        Assert.True(runner.IsCurrent(3));

        firstRelease.SetResult();
        await latestStarted.Task;
        Assert.Equal(new[] { 1, 3 }, calls);
        Assert.Equal(new long[] { 1, 3 }, versions);
        Assert.Equal(1, maximumActiveCount);
        latestRelease.SetResult();
        await run;
        Assert.False(runner.IsRunning);
    }

    [Fact]
    public async Task FailedRunPropagatesFailureDropsPendingAndCanRestart()
    {
        var release = Signal();
        var calls = new List<int>();
        var expected = new InvalidOperationException("query failed");
        var runner = new LatestRequestRunner<int>(async (request, _, _) =>
        {
            calls.Add(request);
            if (request == 1)
            {
                await release.Task;
                throw expected;
            }
        });

        var failed = runner.RequestAsync(1);
        Assert.Same(failed, runner.RequestAsync(2));
        release.SetResult();
        Assert.Same(expected, await Assert.ThrowsAsync<InvalidOperationException>(() => failed));
        Assert.False(runner.IsRunning);
        Assert.True(runner.Completion.IsFaulted);

        await runner.RequestAsync(3);
        Assert.Equal(new[] { 1, 3 }, calls);
        Assert.False(runner.IsRunning);
        Assert.True(runner.Completion.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task SynchronousCallbackFailureDoesNotLeaveRunnerBusy()
    {
        var expected = new InvalidOperationException("UI preparation failed before the first await");
        var runner = new LatestRequestRunner<int>((request, _, _) =>
        {
            if (request == 1) throw expected;
            return Task.CompletedTask;
        });

        var failed = runner.RequestAsync(1);
        Assert.Same(expected, await Assert.ThrowsAsync<InvalidOperationException>(() => failed));
        Assert.False(runner.IsRunning);
        await runner.RequestAsync(2);
        Assert.True(runner.Completion.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task CallbackCancellationIsObservableAndDoesNotPermanentlyStopRunner()
    {
        using var queryCancellation = new CancellationTokenSource();
        queryCancellation.Cancel();
        var runner = new LatestRequestRunner<int>((request, _, _) => request == 1
            ? Task.FromCanceled(queryCancellation.Token)
            : Task.CompletedTask);

        var cancelled = runner.RequestAsync(1);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.True(cancelled.IsCanceled);
        Assert.False(runner.IsRunning);
        await runner.RequestAsync(2);
        Assert.True(runner.Completion.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task SynchronousCallbacksLeaveCompletedStateAndCanRunAgain()
    {
        var calls = new List<int>();
        var runner = new LatestRequestRunner<int>((request, _, _) =>
        {
            calls.Add(request);
            return Task.CompletedTask;
        });

        var first = runner.RequestAsync(1);
        Assert.True(first.IsCompletedSuccessfully);
        Assert.Same(first, runner.Completion);
        Assert.False(runner.IsRunning);
        await runner.RequestAsync(2);
        Assert.Equal(new[] { 1, 2 }, calls);
        Assert.Equal(2L, runner.Version);
    }

    [Fact]
    public async Task SynchronousReentrantRequestUsesEstablishedCompletion()
    {
        var calls = new List<int>();
        Task? reentrant = null;
        LatestRequestRunner<int>? runner = null;
        runner = new LatestRequestRunner<int>((request, _, _) =>
        {
            calls.Add(request);
            if (request == 1)
                reentrant = runner!.RequestAsync(2);
            return Task.CompletedTask;
        });

        var run = runner.RequestAsync(1);
        await run;
        Assert.Same(run, reentrant);
        Assert.Equal(new[] { 1, 2 }, calls);
        Assert.False(runner.IsRunning);
    }

    [Fact]
    public async Task LifetimeCancellationCancelsCurrentWorkDropsPendingAndRejectsNewRequests()
    {
        using var lifetime = new CancellationTokenSource();
        var release = Signal();
        var calls = new List<int>();
        var runner = new LatestRequestRunner<int>(async (request, _, token) =>
        {
            Assert.Equal(lifetime.Token, token);
            calls.Add(request);
            await release.Task.WaitAsync(token);
        }, lifetime.Token);

        var run = runner.RequestAsync(1);
        _ = runner.RequestAsync(2);
        lifetime.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.False(runner.IsRunning);
        Assert.False(runner.IsCurrent(runner.Version));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RequestAsync(3));
        Assert.Equal(new[] { 1 }, calls);
    }

    [Fact]
    public async Task CallbackRunsAndResumesOnRequestSynchronizationContext()
    {
        var context = new QueuedSynchronizationContext();
        var release = Signal();
        var observed = new List<SynchronizationContext?>();
        var runner = new LatestRequestRunner<int>(async (_, _, _) =>
        {
            observed.Add(SynchronizationContext.Current);
            await release.Task;
            observed.Add(SynchronizationContext.Current);
        });

        var run = context.Invoke(() => runner.RequestAsync(1));
        release.SetResult();
        await context.PumpUntilAsync(run);
        await run;
        Assert.Equal(2, observed.Count);
        Assert.All(observed, item => Assert.Same(context, item));
    }

    [Fact]
    public async Task PendingCallbackIsPostedToItsOwnRequestSynchronizationContext()
    {
        var pendingContext = new QueuedSynchronizationContext();
        var firstRelease = Signal();
        var secondStarted = Signal();
        var secondRelease = Signal();
        SynchronizationContext? secondObserved = null;
        var runner = new LatestRequestRunner<int>(async (request, _, _) =>
        {
            if (request == 1)
                await firstRelease.Task;
            else
            {
                secondObserved = SynchronizationContext.Current;
                secondStarted.SetResult();
                await secondRelease.Task;
            }
        });

        var run = runner.RequestAsync(1);
        Assert.Same(run, pendingContext.Invoke(() => runner.RequestAsync(2)));
        firstRelease.SetResult();
        await pendingContext.PumpUntilAsync(secondStarted.Task);
        Assert.Same(pendingContext, secondObserved);
        secondRelease.SetResult();
        // The posted callback completes on pendingContext, while the run itself
        // resumes on the original caller's context. Pump only the posted callback.
        await pendingContext.PumpOneAsync();
        await run;
        Assert.False(runner.IsRunning);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class QueuedSynchronizationContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> work = new();
        private readonly SemaphoreSlim available = new(0);

        public override void Post(SendOrPostCallback callback, object? state)
        {
            work.Enqueue((callback, state));
            available.Release();
        }

        public T Invoke<T>(Func<T> action)
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try { return action(); }
            finally { SetSynchronizationContext(previous); }
        }

        public async Task PumpUntilAsync(Task completion)
        {
            while (!completion.IsCompleted)
                await PumpOneAsync();
        }

        public async Task PumpOneAsync()
        {
            await available.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(work.TryDequeue(out var item));
            Invoke(() =>
            {
                item.Callback(item.State);
                return true;
            });
        }
    }
}
