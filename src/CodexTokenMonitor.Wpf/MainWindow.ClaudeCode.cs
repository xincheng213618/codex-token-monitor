using System.Windows;

namespace CodexTokenMonitor;

public partial class MainWindow
{
    /// <summary>
    /// Rebuilds the Claude Code header from local transcripts. Claude Code logs
    /// no account quota, so the panel shows the 5h block those limits are
    /// metered in, inferred from request times, and capacity estimates from
    /// the user's own /usage readings.
    /// </summary>
    private async Task RefreshClaudeActivityCoreAsync()
    {
        if (isQuotaRefreshing || CurrentModule() is not ClaudeCodeUsageModule claudeModule)
        {
            return;
        }

        isQuotaRefreshing = true;
        try
        {
            var (snapshot, readings) = await Task.Run(
                () => (ClaudeCodeActivityReader.ReadCurrent(BeijingClock.Now, runtime.LifetimeToken), ClaudeQuotaReadingStore.Load()),
                runtime.LifetimeToken);
            if (!isClosed && CurrentModule() is ClaudeCodeUsageModule current && ReferenceEquals(current, claudeModule))
            {
                claudeModule.CurrentActivity = snapshot;
                claudeModule.QuotaReadings = readings;
                ApplyClaudeActivitySummary(claudeModule);
            }
        }
        catch (OperationCanceledException) when (runtime.IsStopping || isClosed)
        {
            // Window shutdown cancels the transcript scan.
        }
        catch (Exception ex)
        {
            if (!isClosed)
            {
                SetStatus($"Claude Code 区块刷新失败：{ex.Message}");
            }
        }
        finally
        {
            isQuotaRefreshing = false;
        }
    }

    private void ApplyClaudeActivitySummary(ClaudeCodeUsageModule module)
    {
        var fiveHour = ClaudeQuotaEstimator.Summarize(module.QuotaReadings, ClaudeQuotaWindowKind.FiveHour);
        var week = ClaudeQuotaEstimator.Summarize(module.QuotaReadings, ClaudeQuotaWindowKind.Week);
        if (module.CurrentActivity is not { } activity)
        {
            ClaudeBlockValue.Text = "--";
            ClaudeBlockDetail.Text = "读取 Claude Code 本地日志…";
            ClaudeBlockCostValue.Text = "-";
            ClaudeBlockCostDetail.Text = null;
            ClaudeModelValue.Text = "-";
            ClaudeModelDetail.Text = null;
            ApplyClaudeCapacity(ClaudeFiveHourCapacityValue, ClaudeFiveHourCapacityDetail, fiveHour, null, "本区块");
            ApplyClaudeCapacity(ClaudeWeekValue, ClaudeWeekDetail, week, null, "近 7 天");
            return;
        }

        var now = activity.NowLocal;
        var block = activity.CurrentBlock;
        if (block is not null)
        {
            ClaudeBlockValue.Text = FormatTokenAdaptive(block.Usage.TotalTokens);
            ClaudeBlockDetail.Text = $"{block.StartLocal:HH:mm}–{block.EndLocal:HH:mm} · {FormatDuration(block.EndLocal - now)}后重置 · {block.Usage.Events:N0} 次请求";
            ClaudeBlockCostValue.Text = FormatClaudeCost(block.Usage);
            ClaudeBlockCostDetail.Text = block.TokensPerMinute is { } rate
                ? $"{FormatTokenAdaptive((long)rate)}/分钟 · 照此到 {block.EndLocal:HH:mm} 约 {FormatTokenAdaptive(block.ProjectedTotalTokens(now) ?? block.Usage.TotalTokens)}"
                : "请求不足 1 分钟，暂无速率";
        }
        else
        {
            ClaudeBlockValue.Text = "空闲";
            ClaudeBlockDetail.Text = activity.LastBlock is { } last
                ? $"上个区块 {last.StartLocal:MM-dd HH:mm} · {FormatTokenAdaptive(last.Usage.TotalTokens)} · 最后请求 {last.LastActivityLocal:HH:mm}"
                : "下次请求开启新的 5h 区块";
            ClaudeBlockCostValue.Text = activity.LastBlock is { } previous ? FormatClaudeCost(previous.Usage) : "-";
            ClaudeBlockCostDetail.Text = activity.LastBlock is null ? "近 24 小时没有请求" : "上个区块费用";
        }

        ApplyClaudeCapacity(ClaudeFiveHourCapacityValue, ClaudeFiveHourCapacityDetail, fiveHour, block?.Usage, "本区块");
        ApplyClaudeCapacity(ClaudeWeekValue, ClaudeWeekDetail, week, activity.Week, "近 7 天");
        if (week is null && activity.WeekActiveDays > 0)
        {
            ClaudeWeekDetail.Text = $"近 7 天 {FormatTokenAdaptive(activity.Week.TotalTokens)} · {FormatClaudeCost(activity.Week)} · 活跃 {activity.WeekActiveDays} 天";
        }

        ApplyClaudeModelMix(activity.Today);
    }

    /// <summary>
    /// Shows the median full-window capacity and, when usage for that window is
    /// at hand, roughly how much of it the Claude models have used so far.
    /// </summary>
    private static void ApplyClaudeCapacity(
        System.Windows.Controls.TextBlock value,
        System.Windows.Controls.TextBlock detail,
        ClaudeQuotaCapacity? capacity,
        TokenUsageBucket? used,
        string usedLabel)
    {
        if (capacity is null)
        {
            value.Text = "未录入";
            detail.Text = "点“额度估算”录入 /usage 百分比";
            return;
        }

        value.Text = ClaudeQuotaWindow.FormatUsd(capacity.CapacityUsd) + (capacity.IsTentative ? "?" : "");
        var parts = new List<string> { $"≈ {FormatTokenAdaptive(capacity.CapacityTokens)} Token" };
        if (used is not null && capacity.CapacityUsd > 0)
        {
            var spent = ClaudeQuotaEstimator.UsdCost(ClaudeQuotaEstimator.ClaudeModelUsage(new[] { used }, used.StartLocal, DateTimeOffset.MaxValue));
            parts.Add($"{usedLabel}约用 {spent / capacity.CapacityUsd * 100:N0}%");
        }

        parts.Add($"{capacity.Windows} 个窗口");
        detail.Text = string.Join(" · ", parts);
    }

    private void ClaudeQuotaButton_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentModule() is not ClaudeCodeUsageModule module)
        {
            return;
        }

        var window = new ClaudeQuotaWindow(module.CurrentActivity?.CurrentBlock?.EndLocal) { Owner = this };
        window.ShowDialog();
        if (window.Changed)
        {
            module.QuotaReadings = ClaudeQuotaReadingStore.Load();
            ApplyClaudeActivitySummary(module);
        }
    }

    private void ApplyClaudeModelMix(TokenUsageBucket today)
    {
        var models = today.ModelUsage
            .Where(pair => pair.Value.TotalTokens > 0 && pair.Key != "<synthetic>")
            .OrderByDescending(pair => pair.Value.TotalTokens)
            .ToList();
        if (models.Count == 0 || today.TotalTokens <= 0)
        {
            ClaudeModelValue.Text = "-";
            ClaudeModelDetail.Text = "今天还没有请求";
            return;
        }

        string Share(KeyValuePair<string, TokenUsageBucket> pair) =>
            $"{ShortClaudeModel(pair.Key)} {pair.Value.TotalTokens * 100d / today.TotalTokens:N0}%";

        ClaudeModelValue.Text = Share(models[0]);
        ClaudeModelDetail.Text = models.Count == 1
            ? $"{today.Events:N0} 次请求 · 缓存命中 {today.CacheRatioPercent:N1}%"
            : string.Join(" · ", models.Skip(1).Take(3).Select(Share));
    }

    // "claude-opus-5-5" reads as "Opus 5.5"; gateway ids such as mimo stay as-is.
    private static string ShortClaudeModel(string modelId)
    {
        if (!modelId.StartsWith("claude-", StringComparison.OrdinalIgnoreCase))
        {
            return modelId;
        }

        var parts = modelId["claude-".Length..].Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !parts.Skip(1).All(part => part.All(char.IsDigit)))
        {
            return modelId["claude-".Length..];
        }

        var family = char.ToUpperInvariant(parts[0][0]) + parts[0][1..];
        return parts.Length == 1 ? family : $"{family} {string.Join('.', parts.Skip(1))}";
    }

    /// <summary>
    /// Headline cost in USD API-equivalent only. Plan presets priced in other
    /// units (MiMo Credits) cannot be added to dollars, so they are named
    /// beside the figure instead of being summed into an unreadable total.
    /// </summary>
    private static string FormatClaudeCost(TokenUsageBucket usage)
    {
        var estimate = CodexModelCost.Estimate(usage, PricePresetGroups.ClaudeCode);
        var usd = estimate.CostTotals.Where(total => total.CurrencySymbol == "$").Sum(total => total.Amount);
        var otherUnits = estimate.CostTotals.Any(total => total.CurrencySymbol != "$");
        var unpriced = estimate.Models.Any(line => line.Cost is null) || estimate.MissingModelEvents > 0;
        var text = otherUnits && usd == 0 ? "按套餐计" : ModelCostEstimate.FormatAmount(usd, "$", "N2");
        return text + (otherUnits && usd > 0 ? "+套餐" : "") + (unpriced ? "*" : "");
    }
}
