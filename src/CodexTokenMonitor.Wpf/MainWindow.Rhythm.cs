using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace CodexTokenMonitor;

public partial class MainWindow
{
    private CodexMessageHistory? messageHistory;
    private CodexMessageHistory? geminiMessageHistory;
    private CodexRhythmWindow? rhythmWindow;
    private readonly DispatcherTimer rhythmTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromHours(1) };
    private Task rhythmRefreshTask = Task.CompletedTask;
    private string? rhythmRefreshError;
    private string? geminiRhythmRefreshError;
    private CancellationTokenSource? rhythmAnalysisRequest;
    private CancellationTokenSource? rhythmTodayRequest;
    private DateTimeOffset? rhythmTodayCutoff;

    internal static bool SupportsUsageRhythm(UsageSource source) =>
        source is UsageSource.Codex or UsageSource.Gemini;

    private CodexMessageHistory? GetRhythmHistory(UsageSource source) =>
        source == UsageSource.Gemini ? geminiMessageHistory : messageHistory;

    private string? GetRhythmError(UsageSource source) =>
        source == UsageSource.Gemini ? geminiRhythmRefreshError : rhythmRefreshError;

    private void SyncOpenRhythmWindowHistory()
    {
        if (rhythmWindow is not { } window) return;
        if (GetRhythmHistory(window.RhythmSource) is { } history)
            window.ShowHistory(history, dayTimeZone.Offset);
    }

    private void InitializeRhythmRefresh()
    {
        rhythmTimer.Tick += async (_, _) => await RefreshRhythmAsync();
        Loaded += async (_, _) =>
        {
            if (isClosed || rhythmTimer.IsEnabled) return;
            rhythmTimer.Start();
            await RefreshRhythmAsync();
        };
    }

    private Task RefreshRhythmAsync()
    {
        if (isClosed || runtime.IsStopping) return Task.CompletedTask;
        if (rhythmRefreshTask.IsCompleted)
            rhythmRefreshTask = RefreshRhythmCoreAsync();
        return rhythmRefreshTask;
    }

    private async Task RefreshRhythmCoreAsync()
    {
        rhythmRefreshError = null;
        geminiRhythmRefreshError = null;
        rhythmWindow?.ShowRefreshStatus(true, null);
        try
        {
            await runtime.Run("使用节律刷新", async token =>
            {
                if (messageHistory is null)
                {
                    var savedCodex = await Task.Run(CodexRhythmReader.ReadSaved, token);
                    if (isClosed) return;
                    if (savedCodex is not null) ApplyRhythmHistory(UsageSource.Codex, savedCodex);
                }

                if (geminiMessageHistory is null)
                {
                    var savedGemini = await Task.Run(() => GeminiRhythmReader.ReadSaved(), token);
                    if (isClosed) return;
                    if (savedGemini is not null) ApplyRhythmHistory(UsageSource.Gemini, savedGemini);
                }

                try
                {
                    var refreshedCodex = await Task.Run(() => CodexRhythmReader.Read(DateTimeOffset.UtcNow, token), token);
                    if (!isClosed) ApplyRhythmHistory(UsageSource.Codex, refreshedCodex);
                }
                catch (OperationCanceledException) when (isClosed || runtime.IsStopping || token.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    Trace.TraceError($"Codex rhythm refresh: {ex}");
                    rhythmRefreshError = ex.Message;
                }

                try
                {
                    var refreshedGemini = await Task.Run(() => GeminiRhythmReader.Read(DateTimeOffset.UtcNow, token), token);
                    if (!isClosed) ApplyRhythmHistory(UsageSource.Gemini, refreshedGemini);
                }
                catch (OperationCanceledException) when (isClosed || runtime.IsStopping || token.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    Trace.TraceError($"Gemini rhythm refresh: {ex}");
                    geminiRhythmRefreshError = ex.Message;
                }

                if (!isClosed)
                {
                    await RefreshRhythmAnalysisAsync();
                }
            });
        }
        catch (OperationCanceledException) when (isClosed || runtime.IsStopping) { }
        catch (Exception error)
        {
            Trace.TraceError($"Usage rhythm refresh: {error}");
            rhythmRefreshError ??= error.Message;
            geminiRhythmRefreshError ??= error.Message;
        }
        finally
        {
            if (!isClosed && rhythmWindow is { } window)
                window.ShowRefreshStatus(false, GetRhythmError(window.RhythmSource));
        }
    }

    private void ApplyRhythmHistory(UsageSource source, CodexMessageHistory history)
    {
        if (source == UsageSource.Gemini)
        {
            geminiMessageHistory = history;
            if (CurrentModule() is GeminiUsageModule geminiModule)
                ApplyGeminiActivitySummary(geminiModule);
        }
        else
        {
            messageHistory = history;
        }

        if (CurrentSource() == source)
            UpdateInteractionCount(CurrentSource(), GetSelectedRange());

        if (rhythmWindow is { } window && window.RhythmSource == source)
            window.ShowHistory(history, dayTimeZone.Offset);
    }

    private async Task RefreshRhythmAnalysisAsync()
    {
        if (isClosed || runtime.IsStopping || rhythmWindow is not { } window)
            return;
        var rhythmSource = window.RhythmSource;
        if (GetRhythmHistory(rhythmSource) is not { } history)
            return;

        rhythmAnalysisRequest?.Cancel();
        using var request = new CancellationTokenSource();
        rhythmAnalysisRequest = request;
        var offset = dayTimeZone.Offset;
        window.ShowAnalysisStatus("正在读取同一时段的 Token…");
        try
        {
            var result = await runtime.Run("交互分析", async lifetime =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime, request.Token);
                var token = linked.Token;
                return await Task.Run(() =>
                {
                    var rhythm = CodexUsageRhythm.Calculate(history, offset);
                    var tokens = rhythmSource == UsageSource.Gemini
                        ? ((GeminiUsageReader)UsageSourceReaders.For(UsageSource.Gemini)).ReadRhythmTokens(rhythm, offset, token)
                        : UsageSourceReaders.Codex.ReadRhythmTokens(rhythm, offset, token);
                    var analysis = CodexRhythmWindowAnalysis.Calculate(history, rhythm, offset,
                        tokens.DailyTotals, tokens.Events, token);
                    return (Analysis: analysis, tokens.DailyTotals);
                }, token);
            });

            if (!isClosed && ReferenceEquals(rhythmAnalysisRequest, request) && !request.IsCancellationRequested &&
                rhythmWindow is { } activeWindow && activeWindow.RhythmSource == rhythmSource)
            {
                activeWindow.ShowAnalysis(result.Analysis, result.DailyTotals);
            }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested || isClosed || runtime.IsStopping) { }
        catch (Exception error)
        {
            Trace.TraceError($"{rhythmSource} rhythm analysis: {error}");
            if (!isClosed && ReferenceEquals(rhythmAnalysisRequest, request) && !request.IsCancellationRequested)
                window.ShowAnalysisStatus("交互分析读取失败，可点击更新统计重试。");
        }
        finally
        {
            if (ReferenceEquals(rhythmAnalysisRequest, request))
                rhythmAnalysisRequest = null;
        }
    }

    private Task CaptureRhythmTodayAsync(DateTimeOffset cutoff)
    {
        rhythmTodayCutoff = cutoff;
        return RefreshRhythmTodayAsync();
    }

    private async Task RefreshRhythmTodayAsync()
    {
        if (isClosed || runtime.IsStopping || rhythmWindow is not { } window || rhythmTodayCutoff is not { } cutoff)
            return;
        var rhythmSource = window.RhythmSource;

        rhythmTodayRequest?.Cancel();
        using var request = new CancellationTokenSource();
        rhythmTodayRequest = request;
        var offset = dayTimeZone.Offset;
        window.ShowTodaySnapshot(null);
        try
        {
            var currentHist = GetRhythmHistory(rhythmSource);
            if (currentHist is null || currentHist.UpdatedAt < cutoff)
            {
                await RefreshRhythmAsync();
                currentHist = GetRhythmHistory(rhythmSource);
                if (!request.IsCancellationRequested && GetRhythmError(rhythmSource) is null &&
                    (currentHist is null || currentHist.UpdatedAt < cutoff))
                {
                    await RefreshRhythmAsync();
                }
            }
            if (request.IsCancellationRequested || isClosed || runtime.IsStopping) return;

            currentHist = GetRhythmHistory(rhythmSource);
            var history = currentHist is { } read && read.UpdatedAt >= cutoff
                ? read : new CodexMessageHistory([], [], cutoff, string.Empty, 0);
            var snapshot = await runtime.Run("今日节律快照", async lifetime =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime, request.Token);
                var token = linked.Token;
                await usageQueryGate.WaitAsync(token);
                try
                {
                    var total = await Task.Run(() => rhythmSource == UsageSource.Gemini
                        ? ((GeminiUsageReader)UsageSourceReaders.For(UsageSource.Gemini)).ReadRhythmTodayTotal(cutoff, offset, token)
                        : UsageSourceReaders.Codex.ReadRhythmTodayTotal(cutoff, offset, token), token);
                    return CodexRhythmToday.Calculate(history, cutoff, offset, total);
                }
                finally
                {
                    usageQueryGate.Release();
                }
            });
            if (!isClosed && ReferenceEquals(rhythmTodayRequest, request) && !request.IsCancellationRequested &&
                rhythmWindow is { } activeWindow && activeWindow.RhythmSource == rhythmSource)
            {
                activeWindow.ShowTodaySnapshot(snapshot);
            }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested || isClosed || runtime.IsStopping) { }
        catch (Exception error)
        {
            Trace.TraceError($"{rhythmSource} today rhythm snapshot: {error}");
            if (!isClosed && ReferenceEquals(rhythmTodayRequest, request) && !request.IsCancellationRequested)
            {
                var currentHist = GetRhythmHistory(rhythmSource);
                var history = currentHist is { } read && read.UpdatedAt >= cutoff
                    ? read : new CodexMessageHistory([], [], cutoff, string.Empty, 0);
                window.ShowTodaySnapshot(CodexRhythmToday.Calculate(history, cutoff, offset, totalTokens: null));
            }
        }
        finally
        {
            if (ReferenceEquals(rhythmTodayRequest, request))
                rhythmTodayRequest = null;
        }
    }

    private void UpdateInteractionCount(UsageSource source, SelectedRange range)
    {
        var supportsRhythm = SupportsUsageRhythm(source);
        CacheWriteTile.Visibility = supportsRhythm ? Visibility.Collapsed : Visibility.Visible;
        InteractionCountButton.Visibility = supportsRhythm ? Visibility.Visible : Visibility.Collapsed;
        EmptyRhythmButton.Visibility = InteractionCountButton.Visibility;
        if (!supportsRhythm) return;

        var title = UsageSourceRegistry.For(source).Title;
        EmptyRhythmButton.Content = $"查看 {title} 使用节律";
        var history = GetRhythmHistory(source);
        if (history is null)
        {
            InteractionCountValue.Text = "—";
            InteractionCountButton.ToolTip = $"打开 {title} 使用节律。程序运行时每小时更新交互次数。";
            return;
        }

        var end = range.End < history.UpdatedAt ? range.End : history.UpdatedAt;
        var count = history.CountInRange(range.Start, end);
        var observed = count > 0 || history.TurnStarts.Any(stamp => stamp >= range.Start && stamp < end);
        InteractionCountValue.Text = observed ? count.ToString("N0") : "—";
        InteractionCountButton.ToolTip = $"当前范围内记录的人工消息 · 更新于 {history.UpdatedAt.ToOffset(dayTimeZone.Offset):MM-dd HH:mm:ss}" +
            (observed ? "\n点击查看详情" : "\n当前范围缺少可确认的会话记录");
    }

    private async void InteractionCountButton_Click(object sender, RoutedEventArgs e)
    {
        var source = SupportsUsageRhythm(CurrentSource()) ? CurrentSource() : UsageSource.Codex;
        await OpenRhythmWindowAsync(source);
    }

    private async Task OpenRhythmWindowAsync(UsageSource source)
    {
        if (isClosed) return;
        var cutoff = DateTimeOffset.UtcNow;
        if (rhythmWindow is not null)
        {
            var switched = rhythmWindow.RhythmSource != source;
            rhythmWindow.SetRhythmSource(source);
            if (switched)
            {
                if (GetRhythmHistory(source) is { } existing)
                    rhythmWindow.ShowHistory(existing, dayTimeZone.Offset);
                rhythmWindow.ShowRefreshStatus(!rhythmRefreshTask.IsCompleted, GetRhythmError(source));
                _ = RefreshRhythmAnalysisAsync();
            }

            if (rhythmWindow.WindowState == WindowState.Minimized) rhythmWindow.WindowState = WindowState.Normal;
            rhythmWindow.Activate();
            await CaptureRhythmTodayAsync(cutoff);
            return;
        }

        var window = new CodexRhythmWindow(() => CaptureRhythmTodayAsync(DateTimeOffset.UtcNow), source) { Owner = this };
        if (GetRhythmHistory(source) is { } history)
            window.ShowHistory(history, dayTimeZone.Offset);
        window.ShowRefreshStatus(!rhythmRefreshTask.IsCompleted, GetRhythmError(source));
        rhythmWindow = window;
        window.Closed += (_, _) =>
        {
            rhythmWindow = null;
            rhythmTodayCutoff = null;
            rhythmAnalysisRequest?.Cancel();
            rhythmTodayRequest?.Cancel();
        };
        window.Show();
        _ = RefreshRhythmAnalysisAsync();
        await CaptureRhythmTodayAsync(cutoff);
    }
}
