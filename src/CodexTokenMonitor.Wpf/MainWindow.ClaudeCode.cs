using System.Windows;
using System.Windows.Controls;

namespace CodexTokenMonitor;

public partial class MainWindow
{
    /// <summary>
    /// Rebuilds the Claude Code header. Quota windows come from the status
    /// line's official rate_limits when recorded, else from the user's /usage
    /// readings, else from the block inferred from request times; every share
    /// and spend figure is measured over that same window.
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
                () =>
                {
                    var readings = ClaudeQuotaReadingStore.Load();
                    var snapshot = ClaudeCodeActivityReader.ReadCurrent(BeijingClock.Now, readings, runtime.LifetimeToken);
                    if (ClaudeQuotaEstimator.RecordOfficial(snapshot, readings))
                    {
                        readings = ClaudeQuotaReadingStore.Load();
                    }

                    return (snapshot, readings);
                },
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
                SetStatus($"Claude Code 额度刷新失败：{ex.Message}");
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
            ApplyClaudeCapacity(ClaudeFiveHourCapacityValue, ClaudeFiveHourCapacityDetail, fiveHour, null);
            ApplyClaudeCapacity(ClaudeWeekValue, ClaudeWeekDetail, week, null);
            return;
        }

        var now = activity.NowLocal;
        var window = activity.FiveHourWindow;
        if (window is not null)
        {
            var remaining = $"{FormatDuration(window.ResetLocal - now)}后重置";
            if (window.OfficialPercent is { } percent)
            {
                ClaudeBlockValue.Text = $"已用 {percent:0.#}%";
                ClaudeBlockDetail.Text = $"官方 · {window.ResetLocal:HH:mm} 重置 · {remaining} · 本机 {FormatTokenAdaptive(window.Usage.TotalTokens)}";
            }
            else
            {
                ClaudeBlockValue.Text = FormatTokenAdaptive(window.Usage.TotalTokens);
                ClaudeBlockDetail.Text = $"{(window.Source == ClaudeWindowSource.Manual ? "按录入" : "推算")} · {window.StartLocal:HH:mm}–{window.ResetLocal:HH:mm} · {remaining} · {window.Usage.Events:N0} 次请求";
            }

            ClaudeBlockCostValue.Text = FormatClaudeCost(window.Usage);
            ClaudeBlockCostDetail.Text = activity.CurrentBlock is { TokensPerMinute: { } rate } block
                ? $"{FormatTokenAdaptive((long)rate)}/分钟 · 照此到 {block.EndLocal:HH:mm} 约 {FormatTokenAdaptive(block.ProjectedTotalTokens(now) ?? block.Usage.TotalTokens)}"
                : "本窗口内的 API 等价费用";
        }
        else
        {
            ClaudeBlockValue.Text = "空闲";
            ClaudeBlockDetail.Text = activity.LastBlock is { } last
                ? $"上个区块 {last.StartLocal:MM-dd HH:mm} · {FormatTokenAdaptive(last.Usage.TotalTokens)} · 最后请求 {last.LastActivityLocal:HH:mm}"
                : "下次请求开启新的 5h 窗口";
            ClaudeBlockCostValue.Text = activity.LastBlock is { } previous ? FormatClaudeCost(previous.Usage) : "-";
            ClaudeBlockCostDetail.Text = activity.LastBlock is null ? "近 24 小时没有请求" : "上个区块费用";
        }

        ApplyClaudeCapacity(ClaudeFiveHourCapacityValue, ClaudeFiveHourCapacityDetail, fiveHour, window);
        ApplyClaudeCapacity(ClaudeWeekValue, ClaudeWeekDetail, week, activity.WeekWindow);
        if (activity.WeekWindow is null && (week is null || activity.WeekActiveDays > 0))
        {
            // Without a known weekly reset there is no window to measure; the
            // calendar total is shown as context only, never extrapolated.
            var prefix = week is null ? "" : $"{ClaudeWeekDetail.Text} · ";
            ClaudeWeekDetail.Text = prefix + $"近 7 天 {FormatTokenAdaptive(activity.Week.TotalTokens)} · {FormatClaudeCost(activity.Week)}" +
                                    (activity.WeekIsComplete ? $" · 活跃 {activity.WeekActiveDays} 天" : " · 数据不完整");
        }

        ApplyClaudeModelMix(activity.Today);
    }

    /// <summary>
    /// Shows the median full-window capacity and how much of the live window
    /// is used: the official percentage when known, otherwise the window's
    /// Claude-model spend over capacity. An incomplete window is not
    /// extrapolated.
    /// </summary>
    private static void ApplyClaudeCapacity(TextBlock value, TextBlock detail, ClaudeQuotaCapacity? capacity, ClaudeQuotaWindowUsage? window)
    {
        var used = window switch
        {
            null => null,
            { OfficialPercent: { } percent } => $"已用 {percent:0.#}%（官方）",
            { IsComplete: false } => "数据不完整，暂不外推",
            _ when capacity is { CapacityUsd: > 0 } =>
                $"约用 {ClaudeQuotaEstimator.UsdCost(window.ClaudeUsage) / capacity.CapacityUsd * 100:N0}%",
            _ => null
        };
        var reset = window is null ? null : $"{window.ResetLocal:MM-dd HH:mm} 重置";

        if (capacity is null)
        {
            value.Text = "未录入";
            detail.Text = string.Join(" · ", new[] { used, used is null ? "点“额度估算”录入 /usage" : reset }.Where(part => part is not null));
            return;
        }

        value.Text = ClaudeQuotaWindow.FormatUsd(capacity.CapacityUsd) + (capacity.IsTentative ? "?" : "");
        detail.Text = string.Join(" · ", new[]
        {
            $"≈ {FormatTokenAdaptive(capacity.CapacityTokens)} Token",
            used,
            $"{capacity.Windows} 个窗口"
        }.Where(part => part is not null));
    }

    private void ClaudeQuotaButton_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentModule() is not ClaudeCodeUsageModule module)
        {
            return;
        }

        var window = new ClaudeQuotaWindow(
            module.CurrentActivity?.FiveHourWindow?.ResetLocal,
            module.CurrentActivity?.WeekWindow?.ResetLocal)
        {
            Owner = this
        };
        window.ShowDialog();
        if (window.Changed)
        {
            module.QuotaReadings = ClaudeQuotaReadingStore.Load();
            ApplyClaudeActivitySummary(module);
            _ = RefreshQuotaSummaryAsync();
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
