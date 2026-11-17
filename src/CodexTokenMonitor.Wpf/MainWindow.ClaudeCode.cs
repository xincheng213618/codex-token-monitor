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
            var (snapshot, readings, quotaFailure) = await Task.Run(
                async () =>
                {
                    var live = await claudeModule.OfficialQuota.ReadAsync(runtime.LifetimeToken);
                    var readings = ClaudeQuotaReadingStore.Load();
                    var snapshot = ClaudeCodeActivityReader.ReadCurrent(BeijingClock.Now, readings, runtime.LifetimeToken, live.Snapshot);
                    if (ClaudeQuotaEstimator.RecordOfficial(snapshot, readings))
                    {
                        readings = ClaudeQuotaReadingStore.Load();
                    }

                    return (snapshot, readings, live.Failure);
                },
                runtime.LifetimeToken);
            if (!isClosed && CurrentModule() is ClaudeCodeUsageModule current && ReferenceEquals(current, claudeModule))
            {
                claudeModule.CurrentActivity = snapshot;
                claudeModule.QuotaReadings = readings;
                claudeModule.QuotaFailure = quotaFailure;
                ApplyClaudeActivitySummary(claudeModule);
                AdvanceClaudeCycle(claudeModule, snapshot.WeekWindow);
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
        var activity = module.CurrentActivity;
        ApplyOfficial(ClaudeBlockValue, ClaudeBlockDetail, activity?.FiveHourWindow);
        ApplyOfficial(ClaudeWeekValue, ClaudeWeekDetail, activity?.WeekWindow);
        var fable = activity?.FableWeek;
        ClaudeFableWeekValue.Text = fable is not null ? $"剩余 {100 - fable.UsedPercent:0.#}%" : activity is null ? "--" : "未获取";
        ClaudeFableWeekDetail.Text = fable is not null
            ? $"{fable.ResetLocal:MM-dd HH:mm} 重置"
            : module.QuotaFailure ?? (activity is null ? "等待官方额度" : "官方响应未提供 Fable 额度");
        ClaudeFableWeekValue.ToolTip = fable is not null
            ? $"Fable 自身周额度已用 {fable.UsedPercent:0.#}% · {fable.ResetLocal:MM-dd HH:mm} 重置" +
              (fable.ObservedLocal is { } at ? $" · 更新 {at:HH:mm:ss}" : "") +
              (module.QuotaFailure is { } failure ? $" · {failure}，显示上次读数" : "")
            : null;
        var reminder = module.FableReset;
        ClaudeFableResetValue.Text = reminder?.Status(BeijingClock.Now) ?? "未登记";
        ClaudeFableResetDetail.Text = reminder is null ? "额度详情内登记"
            : $"{reminder.ExpiresAt.ToOffset(CodexUsageReader.BeijingOffset):MM-dd HH:mm} 到期";
        ApplyClaudeReference(ClaudeFiveHourCapacityValue, ClaudeFiveHourCapacityDetail,
            ClaudeQuotaReferenceCalculator.Estimate(ClaudeQuotaWindowKind.FiveHour, activity?.FiveHourWindow, module.QuotaReadings));
        ApplyClaudeReference(ClaudeWeekCapacityValue, ClaudeWeekCapacityDetail,
            ClaudeQuotaReferenceCalculator.Estimate(ClaudeQuotaWindowKind.Week, activity?.WeekWindow, module.QuotaReadings));
        if (activity is not null) ApplyClaudeModelMix(activity.Today);

        void ApplyOfficial(TextBlock value, TextBlock detail, ClaudeQuotaWindowUsage? window)
        {
            if (window?.OfficialPercent is { } percent)
            {
                value.Text = $"剩余 {100 - percent:0.#}%";
                detail.Text = OfficialDetail(window, module.QuotaFailure);
            }
            else
            {
                value.Text = activity is null ? "--" : "未获取";
                detail.Text = module.QuotaFailure ?? "等待 Claude 官方额度";
            }
        }
    }

    private static string OfficialDetail(ClaudeQuotaWindowUsage window, string? failure) =>
        $"已用 {window.OfficialPercent:0.#}%（官方） · {window.ResetLocal:MM-dd HH:mm} 重置" +
        (window.ObservedLocal is { } at ? $" · 更新 {at:HH:mm:ss}" : "") +
        (failure is null ? "" : $" · {failure}，显示上次读数");

    private static void ApplyClaudeReference(TextBlock value, TextBlock detail, ClaudeQuotaReference? reference)
    {
        if (reference is null)
        {
            value.Text = "待估算";
            detail.Text = "有官方已用比例和 Token 后自动估算";
            return;
        }
        value.Text = "≈ " + ClaudeQuotaWindow.FormatUsd(reference.TotalCostUsd);
        detail.Text = $"≈ {FormatTokenAdaptive(reference.TotalTokens)} Token · " +
            (!reference.IsCurrentWindow ? "历史参考" : reference.IsTentative ? "大致估算" : "自动外推");
    }

    private async void ClaudeQuotaButton_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentModule() is not ClaudeCodeUsageModule module) return;
        var dialog = new ClaudeQuotaWindow(() => module.CurrentActivity, () => module.QuotaReadings, module.FableReset, reminder =>
        {
            module.FableReset = reminder;
            ApplyClaudeActivitySummary(module);
        }) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.OpenCycle is not { } selected || isClosed) return;
        await RunUiActionAsync(async () =>
        {
            SelectClaudeCycle(module, dialog.Cycles, selected);
            await RefreshUsageAsync();
        });
    }

    private void SelectClaudeCycle(ClaudeCodeUsageModule module, IReadOnlyList<ClaudeQuotaCycleUsage> cycles, ClaudeQuotaCycleUsage selected)
    {
        module.QuotaCycles = cycles.Select(cycle => new CodexQuotaCycle(
            cycle.Start, cycle.Reset, cycle.Reset, cycle.Official is null ? 0 : 1, null, cycle.IsCurrent)).ToArray();
        module.SelectedCycle = module.QuotaCycles.First(cycle => cycle.ResetAt == selected.Reset);
        module.CustomStartLocal = null;
        module.Mode = RangeMode.Cycle;
        UpdateRangeControls();
    }

    private void AdvanceClaudeCycle(ClaudeCodeUsageModule module, ClaudeQuotaWindowUsage? window)
    {
        if (module.QuotaCycles.Count == 0 || window is not { Source: ClaudeWindowSource.Official }) return;
        var reset = ClaudeQuotaCycleHistory.NormalizeReset(window.ResetLocal);
        if (reset <= module.QuotaCycles[0].ResetAt) return;
        var followCurrent = module.SelectedCycle?.IsCurrent == true;
        var current = new CodexQuotaCycle(reset.AddDays(-7), reset, reset, 1, null, true);
        module.QuotaCycles = module.QuotaCycles.Select(cycle => cycle with { IsCurrent = false }).Prepend(current).ToArray();
        if (followCurrent) module.SelectedCycle = current;
        if (module.Mode != RangeMode.Cycle) return;
        UpdateRangeControls();
        if (followCurrent) _ = RefreshUsageAsync(isAutomaticRefresh: true);
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

}
