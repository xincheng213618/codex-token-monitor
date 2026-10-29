namespace CodexTokenMonitor;

public partial class MainWindow
{
    /// <summary>
    /// Rebuilds the Claude Code header from local transcripts. Claude Code logs
    /// no account quota, so the panel shows the 5h block those limits are
    /// metered in, inferred from request times, next to today's model mix.
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
            var snapshot = await Task.Run(
                () => ClaudeCodeActivityReader.ReadCurrent(BeijingClock.Now, runtime.LifetimeToken),
                runtime.LifetimeToken);
            if (!isClosed && CurrentModule() is ClaudeCodeUsageModule current && ReferenceEquals(current, claudeModule))
            {
                claudeModule.CurrentActivity = snapshot;
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
        if (module.CurrentActivity is not { } activity)
        {
            ClaudeBlockValue.Text = "--";
            ClaudeBlockDetail.Text = "读取 Claude Code 本地日志…";
            ClaudeBlockCostValue.Text = "-";
            ClaudeBlockCostDetail.Text = null;
            ClaudeModelValue.Text = "-";
            ClaudeModelDetail.Text = null;
            ClaudeWeekValue.Text = "--";
            ClaudeWeekDetail.Text = null;
            ClaudeLastActivityValue.Text = "-";
            ClaudeLastActivityDetail.Text = null;
            return;
        }

        var now = activity.NowLocal;
        if (activity.CurrentBlock is { } block)
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
                ? $"上个区块 {last.StartLocal:MM-dd HH:mm} · {FormatTokenAdaptive(last.Usage.TotalTokens)}"
                : "下次请求开启新的 5h 区块";
            ClaudeBlockCostValue.Text = activity.LastBlock is { } previous ? FormatClaudeCost(previous.Usage) : "-";
            ClaudeBlockCostDetail.Text = activity.LastBlock is null ? "近 24 小时没有请求" : "上个区块费用";
        }

        ApplyClaudeModelMix(activity.Today);

        ClaudeWeekValue.Text = FormatTokenAdaptive(activity.Week.TotalTokens);
        ClaudeWeekDetail.Text = activity.WeekActiveDays == 0
            ? "近 7 天没有请求"
            : $"活跃 {activity.WeekActiveDays} 天 · 日均 {FormatTokenAdaptive(activity.Week.TotalTokens / activity.WeekActiveDays)} · {FormatClaudeCost(activity.Week)}";

        if (activity.LastBlock is { } latest)
        {
            var ago = now - latest.LastActivityLocal;
            ClaudeLastActivityValue.Text = ago < TimeSpan.FromMinutes(1) ? "刚刚" : $"{FormatDuration(ago)}前";
            var at = latest.LastActivityLocal.Date == now.Date
                ? latest.LastActivityLocal.ToString("HH:mm:ss")
                : latest.LastActivityLocal.ToString("MM-dd HH:mm");
            ClaudeLastActivityDetail.Text = $"{at} · 今日 {activity.TodayBlocks} 个区块";
        }
        else
        {
            ClaudeLastActivityValue.Text = "-";
            ClaudeLastActivityDetail.Text = "近 24 小时没有请求";
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
