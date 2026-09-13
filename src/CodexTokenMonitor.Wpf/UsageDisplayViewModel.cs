using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CodexTokenMonitor;

internal enum UsageDisplayStage { Loading, Ready, Empty, Unavailable, Stale }

/// <summary>
/// UI-thread-owned presentation state. It consumes accepted query results and
/// never reads stores, starts tasks, renders charts, or owns source selection.
/// A complete immutable snapshot is published before notifying bindings.
/// </summary>
internal sealed class UsageDisplayViewModel : INotifyPropertyChanged
{
    private readonly TimeProvider clock;
    private bool busy;
    private bool stopped;
    private bool hasSuccessfulResult;
    private bool copySourceAvailable;
    private string sourceTitle = "Codex";

    public UsageDisplayViewModel(TimeProvider? timeProvider = null)
    {
        clock = timeProvider ?? TimeProvider.System;
        Snapshot = LoadingSnapshot(UsageSource.Codex, sourceTitle);
    }

    public UsageDisplaySnapshot Snapshot { get; private set; }
    public UsageDisplayStage Stage { get; private set; } = UsageDisplayStage.Loading;
    public string Status { get; private set; } = "准备就绪";
    public string? StatusDetail { get; private set; }
    public bool ShowContent => Snapshot.HasUsage;
    public bool ShowEmpty => !ShowContent;
    public bool CanCopy => !stopped && !busy && hasSuccessfulResult && copySourceAvailable && Snapshot.HasUsage;
    public string WindowTitle => Snapshot.WindowTitle;

    public void ShowLoading(UsageSource source, string title)
    {
        if (stopped) return;
        sourceTitle = title;
        hasSuccessfulResult = false;
        copySourceAvailable = false;
        Publish(LoadingSnapshot(source, title), UsageDisplayStage.Loading);
        SetStatus($"正在读取 {title}...");
    }

    public void ShowResult(UsageSource source, string title, SelectedRange range, UsageQueryResult result)
    {
        if (stopped) return;
        // Failure-bearing results cannot accidentally become an accepted display
        // through a new UI entry point. The caller separately formats the error.
        if (result.CacheWarnings.Count > 0)
            throw new ArgumentException("A failed query result cannot replace the display.", nameof(result));
        var summary = result.Summary;
        var hasUsage = ContainsUsage(result);
        var now = clock.GetUtcNow().ToOffset(CodexUsageReader.BeijingOffset);
        var isToday = range.Mode == RangeMode.Day && range.Start.Date == now.Date;
        sourceTitle = title;
        hasSuccessfulResult = true;
        copySourceAvailable = true;
        Publish(new UsageDisplaySnapshot
        {
            Source = source,
            HasUsage = hasUsage,
            WindowTitle = $"{title} Token 额度监控器 - {range.Title}",
            Total = UsageDisplayFormatting.TokenMillions(summary.TotalTokens),
            Period = $"{summary.StartLocal:yyyy-MM-dd HH:mm} - {summary.EndLocal:yyyy-MM-dd HH:mm:ss}  GMT+8",
            Input = UsageDisplayFormatting.TokenMillions(summary.InputTokens),
            Cached = UsageDisplayFormatting.TokenMillions(summary.CachedInputTokens),
            CacheWrite = UsageDisplayFormatting.TokenMillions(summary.CacheWriteInputTokens),
            Uncached = UsageDisplayFormatting.TokenMillions(summary.UncachedInputTokens),
            Output = UsageDisplayFormatting.TokenAdaptive(summary.OutputTokens),
            Reasoning = UsageDisplayFormatting.TokenAdaptive(summary.ReasoningOutputTokens),
            CacheRatio = summary.InputTokens > 0 ? $"{summary.CacheRatioPercent:N2}%" : "0.00%",
            Events = summary.Events.ToString("N0"),
            CodingTime = UsageDisplayFormatting.Duration(result.CodingTime),
            EmptyTitle = hasUsage ? "" : isToday ? $"今天还没有 {title} 用量" : $"{range.Title}暂无 {title} 用量",
            EmptyPeriod = hasUsage ? "" : $"{range.Start:yyyy-MM-dd HH:mm} — {range.End:yyyy-MM-dd HH:mm:ss}  GMT+8",
            EmptyHint = hasUsage ? "" : source == UsageSource.Codex
                ? "额度会继续独立刷新；产生首条 Codex 用量后，这里会自动恢复统计卡和明细表。"
                : $"产生首条 {title} 用量后，这里会自动恢复统计卡和明细表。"
        }, hasUsage ? UsageDisplayStage.Ready : UsageDisplayStage.Empty);
        SetStatus("读取完成");
    }

    public void ShowReadFailure(string reason, string? detail)
    {
        if (stopped) return;
        if (hasSuccessfulResult)
        {
            // This also preserves a successful empty result, which is different
            // from having no successful history at all.
            Stage = UsageDisplayStage.Stale;
            Changed(nameof(Stage));
        }
        else
        {
            Publish(Snapshot with
            {
                EmptyTitle = $"{sourceTitle} 统计暂不可用",
                EmptyHint = $"{reason}。恢复后再次刷新即可重试。"
            }, UsageDisplayStage.Unavailable);
        }
        SetStatus($"读取失败：{reason}；" + (hasSuccessfulResult
            ? "当前显示上次成功结果，下次刷新将重试。"
            : "本次统计不可用，下次刷新将重试。"), detail);
    }

    public void SetStatus(string text, string? detail = null)
    {
        if (stopped) return;
        Status = text;
        StatusDetail = detail;
        Changed(nameof(Status));
        Changed(nameof(StatusDetail));
    }

    public void SetBusy(bool value)
    {
        if (stopped || busy == value) return;
        busy = value;
        Changed(nameof(CanCopy));
    }

    // Sharing/import can invalidate the module's copy source without refreshing
    // the visible snapshot. Busy state alone cannot establish copy eligibility.
    public void SetCopySourceAvailable(bool value)
    {
        if (stopped || copySourceAvailable == value) return;
        copySourceAvailable = value;
        Changed(nameof(CanCopy));
    }

    public void Stop()
    {
        if (stopped) return;
        stopped = true;
        Changed(nameof(CanCopy));
    }

    private void Publish(UsageDisplaySnapshot snapshot, UsageDisplayStage stage)
    {
        Snapshot = snapshot;
        Stage = stage;
        Changed(nameof(Snapshot));
        Changed(nameof(Stage));
        Changed(nameof(ShowContent));
        Changed(nameof(ShowEmpty));
        Changed(nameof(WindowTitle));
        Changed(nameof(CanCopy));
    }

    private static UsageDisplaySnapshot LoadingSnapshot(UsageSource source, string title) => new()
    {
        Source = source,
        WindowTitle = $"{title} Token 额度监控器",
        EmptyTitle = $"正在读取 {title} 用量",
        EmptyHint = "完成后会自动显示当前时段的统计结果。"
    };

    internal static bool ContainsUsage(UsageQueryResult result) =>
        result.Summary.Events > 0 || result.Summary.TotalTokens > 0 || result.Summary.InputTokens > 0 ||
        result.Summary.OutputTokens > 0 || result.Summary.ReasoningOutputTokens > 0 ||
        result.BreakdownRows.Any(row => row.Events > 0 || row.TotalTokens > 0 || row.InputTokens > 0 ||
            row.OutputTokens > 0 || row.ReasoningOutputTokens > 0);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

internal sealed record UsageDisplaySnapshot
{
    public UsageSource Source { get; init; }
    public bool HasUsage { get; init; }
    public string WindowTitle { get; init; } = "";
    public string Total { get; init; } = "-";
    public string Period { get; init; } = "-";
    public string Input { get; init; } = "-";
    public string Cached { get; init; } = "-";
    public string CacheWrite { get; init; } = "-";
    public string Uncached { get; init; } = "-";
    public string Output { get; init; } = "-";
    public string Reasoning { get; init; } = "-";
    public string CacheRatio { get; init; } = "-";
    public string Events { get; init; } = "-";
    public string CodingTime { get; init; } = "-";
    public string EmptyTitle { get; init; } = "";
    public string EmptyPeriod { get; init; } = "";
    public string EmptyHint { get; init; } = "";
}

internal static class UsageDisplayFormatting
{
    internal static string TokenMillions(long value) => $"{value / 1_000_000d:N3}M";
    internal static string TokenAdaptive(long value) => value switch
    {
        >= 1_000_000 => TokenMillions(value),
        >= 10_000 => $"{value / 1_000d:N1}K",
        >= 1_000 => $"{value / 1_000d:N2}K",
        _ => value.ToString("N0")
    };
    internal static string Duration(TimeSpan value) => value <= TimeSpan.Zero ? "-" : value.TotalHours >= 1
        ? $"{(int)value.TotalHours}h {value.Minutes}m"
        : $"{Math.Max(1, (int)Math.Round(value.TotalMinutes))}m";
}
