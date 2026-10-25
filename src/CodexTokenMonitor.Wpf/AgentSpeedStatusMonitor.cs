using System.Windows.Threading;

namespace CodexTokenMonitor;

/// <summary>One output-speed sample for the main status bar; independent of the usage query gate.</summary>
internal sealed class AgentSpeedStatusMonitor
{
    private readonly Dictionary<UsageSource, IAgentSpeedSampler> samplers = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly AgentSpeedHistoryStore history = new();
    private UsageSource source;
    private bool active, closed, reading, pending;
    private long version;
    private DateTimeOffset nextPeakSave;
    private Task readTask = Task.CompletedTask, cleanup = Task.CompletedTask;
    private CancellationTokenSource? sampling;
    internal AgentSpeedSnapshot? LastSnapshot { get; private set; }
    internal bool IsAutoRefreshing => timer.IsEnabled;
    internal bool IsReading => reading;
    internal bool IsActive => active;
    internal Task Completion => Task.WhenAll(readTask, cleanup).ContinueWith(_ => { }, TaskScheduler.Default);
    internal event Action<AgentSpeedSnapshot>? Updated;
    internal event Action<Exception>? Failed;

    internal AgentSpeedStatusMonitor() => timer.Tick += async (_, _) => await RefreshAsync();
    internal void Activate(UsageSource selected, bool automatic)
    {
        if (closed) return;
        if (source != selected || !active || LastSnapshot is null)
        {
            if (source != selected) LastSnapshot = null;
            source = selected; version++; nextPeakSave = default; sampling?.Cancel();
        }
        active = true; SetAutoRefresh(automatic); _ = RefreshAsync();
    }
    internal void Deactivate()
    {
        active = false; timer.Stop(); pending = false; version++; sampling?.Cancel();
    }
    internal void SetAutoRefresh(bool enabled)
    { if (enabled && active && !closed) timer.Start(); else timer.Stop(); }
    internal async Task RefreshAsync()
    {
        if (closed || !active) return;
        if (reading) { pending = true; return; }
        reading = true;
        var pass = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        sampling = pass;
        var captured = version;
        try
        {
            if (!samplers.TryGetValue(source, out var sampler)) samplers[source] = sampler = new AgentSpeedMonitor(source: source);
            var selected = source; var now = BeijingClock.Now;
            var day = new DateTimeOffset(now.Date, now.Offset); var token = pass.Token;
            var savePeak = now >= nextPeakSave;
            var task = Task.Run(() =>
            {
                var result = sampler.Read(now, day, token);
                token.ThrowIfCancellationRequested();
                if (savePeak) history.TryRecordPeak(selected, result);
                return result;
            }, token);
            readTask = task; var snapshot = await task;
            if (!closed && active && captured == version)
            {
                if (savePeak) nextPeakSave = now.AddSeconds(30);
                LastSnapshot = snapshot; Updated?.Invoke(snapshot);
            }
        }
        catch (OperationCanceledException) when (pass.IsCancellationRequested) { }
        catch (Exception error) { if (!closed && active && captured == version) Failed?.Invoke(error); }
        finally
        {
            if (ReferenceEquals(sampling, pass)) sampling = null;
            pass.Dispose(); reading = false;
            if (pending && active && !closed) { pending = false; await RefreshAsync(); }
        }
    }
    internal void Stop()
    {
        if (closed) return;
        closed = true; Deactivate(); lifetime.Cancel();
        cleanup = readTask.ContinueWith(_ => { foreach (var sampler in samplers.Values) sampler.Dispose(); lifetime.Dispose(); }, TaskScheduler.Default);
    }
}
