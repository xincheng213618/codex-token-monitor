using System.Windows;
using System.Windows.Controls;

namespace CodexTokenMonitor;

public partial class MainWindow
{
    private async Task RefreshGeminiActivityCoreAsync()
    {
        if (isQuotaRefreshing || CurrentModule() is not GeminiUsageModule geminiModule)
        {
            return;
        }

        var range = GetSelectedRange();
        var shouldRefreshRhythm = geminiMessageHistory is null ||
            DateTimeOffset.UtcNow - geminiMessageHistory.UpdatedAt >= TimeSpan.FromSeconds(15);
        isQuotaRefreshing = true;
        try
        {
            GeminiActivitySnapshot snapshot;
            IReadOnlyList<ClaudeQuotaReading> readings;
            CodexMessageHistory? updatedRhythm = null;
            await usageQueryGate.WaitAsync(runtime.LifetimeToken);
            try
            {
                if (isClosed) return;
                (snapshot, readings, updatedRhythm) = await Task.Run(() =>
                {
                    GeminiDesktopQuotaReader.Synchronize();
                    var loadedReadings = GeminiQuotaReadingStore.Load();
                    var currentSnapshot = GeminiActivityReader.ReadCurrent(
                        range.Start, range.End, BeijingClock.Now, loadedReadings, runtime.LifetimeToken);
                    if (GeminiQuotaEstimator.RecordSnapshot(currentSnapshot, loadedReadings))
                    {
                        loadedReadings = GeminiQuotaReadingStore.Load();
                    }

                    CodexMessageHistory? rhythm = null;
                    if (shouldRefreshRhythm)
                    {
                        try { rhythm = GeminiRhythmReader.Read(DateTimeOffset.UtcNow, runtime.LifetimeToken); }
                        catch (Exception ex) when (ex is not OperationCanceledException) { }
                    }

                    return (currentSnapshot, loadedReadings, rhythm);
                }, runtime.LifetimeToken);
            }
            finally
            {
                usageQueryGate.Release();
            }

            if (!isClosed && updatedRhythm is not null)
            {
                ApplyRhythmHistory(UsageSource.Gemini, updatedRhythm);
            }

            if (!isClosed && CurrentModule() is GeminiUsageModule current && ReferenceEquals(current, geminiModule))
            {
                geminiModule.CurrentActivity = snapshot;
                geminiModule.QuotaReadings = readings;
                ApplyGeminiActivitySummary(geminiModule);
                AdvanceGeminiCycle(geminiModule, snapshot.WeekWindow);
            }
        }
        catch (OperationCanceledException) when (runtime.IsStopping || isClosed)
        {
        }
        catch (Exception ex)
        {
            if (!isClosed)
            {
                SetStatus($"Gemini 额度刷新失败：{ex.Message}");
            }
        }
        finally
        {
            isQuotaRefreshing = false;
        }
    }

    private void ApplyGeminiActivitySummary(GeminiUsageModule module)
    {
        var activity = module.CurrentActivity;
        var fiveRef = GeminiQuotaReferenceCalculator.Estimate(ClaudeQuotaWindowKind.FiveHour, activity?.FiveHourWindow, module.QuotaReadings);
        var weekRef = GeminiQuotaReferenceCalculator.Estimate(ClaudeQuotaWindowKind.Week, activity?.WeekWindow, module.QuotaReadings);

        ApplyGeminiWindow(GeminiBlockValue, GeminiBlockDetail, activity, activity?.FiveHourWindow, fiveRef, isWeek: false);
        ApplyGeminiWindow(GeminiWeekValue, GeminiWeekDetail, activity, activity?.WeekWindow, weekRef, isWeek: true);
        ApplyGeminiReference(GeminiFiveHourCapacityValue, GeminiFiveHourCapacityDetail, fiveRef);
        ApplyGeminiReference(GeminiWeekCapacityValue, GeminiWeekCapacityDetail, weekRef);

        if (activity is not null)
        {
            ApplyGeminiModelMix(activity.Today);
            ApplyGeminiSessionSummary(activity.RangeStats);
        }
        else
        {
            GeminiPrimaryModelValue.Text = "-";
            GeminiPrimaryModelDetail.Text = "等待模型统计";
            GeminiSessionStatsValue.Text = "-";
            GeminiSessionStatsDetail.Text = "正在统计会话…";
        }
    }

    private static void ApplyGeminiWindow(
        TextBlock valueBlock,
        TextBlock detailBlock,
        GeminiActivitySnapshot? activity,
        GeminiQuotaWindowUsage? window,
        ClaudeQuotaReference? reference,
        bool isWeek)
    {
        if (window?.ReportedUsedPercent is { } percent)
        {
            var remaining = Math.Max(0, 100 - percent);
            valueBlock.Text = $"剩余 {remaining:0.#}%";
            if (isWeek && reference is { TotalCostUsd: > 0 })
            {
                detailBlock.Text = $"{window.ResetLabel} 重置（{window.ReadingLabel}） · 满额 ≈{ClaudeQuotaWindow.FormatUsd(reference.TotalCostUsd)}";
            }
            else
            {
                var cost = GeminiQuotaEstimator.UsdCost(window.GeminiUsage);
                detailBlock.Text = $"已用 {percent:0.#}%（{window.ReadingLabel}） · {window.ResetLabel} 重置" +
                                   (cost > 0 ? $" · 已耗 ${cost:N2}" : "");
            }
            return;
        }

        if (activity is null)
        {
            valueBlock.Text = "--";
            detailBlock.Text = "等待 Gemini 额度";
            return;
        }

        valueBlock.Text = "未获取";
        if (!isWeek && window is not null && window.GeminiUsage.Events > 0)
        {
            var blockCost = GeminiQuotaEstimator.UsdCost(window.GeminiUsage);
            detailBlock.Text = $"5h 块已耗 ${blockCost:N2} · {window.ResetLocal:HH:mm} 重置（点额度详情登记%）";
        }
        else if (isWeek && activity.Week.Events > 0)
        {
            var weekCost = GeminiQuotaEstimator.UsdCost(activity.Week);
            detailBlock.Text = $"近 7d 已耗 ${weekCost:N2} · 点「额度详情」登记周额度%";
        }
        else
        {
            detailBlock.Text = "点「额度详情」登记或配 statusLine";
        }
    }

    private static void ApplyGeminiReference(TextBlock value, TextBlock detail, ClaudeQuotaReference? reference)
    {
        if (reference is null)
        {
            value.Text = "待估算";
            detail.Text = "有已用比例和 Token 后自动估算";
            return;
        }

        var tokens = $"≈ {FormatTokenAdaptive(reference.TotalTokens)}";
        var quality = !reference.IsCurrentWindow ? "历史参考" : reference.IsTentative ? "大致估算" : "本机用量外推";
        value.Text = reference.TotalCostUsd > 0 ? "≈ " + ClaudeQuotaWindow.FormatUsd(reference.TotalCostUsd) : tokens;
        detail.Text = (reference.TotalCostUsd > 0 ? $"{tokens} Token · " : "Token · ") + quality;
    }

    private void ApplyGeminiModelMix(TokenUsageBucket today)
    {
        var models = today.ModelUsage
            .Where(pair => pair.Value.TotalTokens > 0)
            .OrderByDescending(pair => pair.Value.TotalTokens)
            .ToList();
        if (models.Count == 0 || today.TotalTokens <= 0)
        {
            GeminiPrimaryModelValue.Text = "-";
            GeminiPrimaryModelDetail.Text = "今天还没有请求";
            return;
        }

        string Share(KeyValuePair<string, TokenUsageBucket> pair) =>
            $"{ShortGeminiModel(pair.Key)} {pair.Value.TotalTokens * 100d / today.TotalTokens:N0}%";

        GeminiPrimaryModelValue.Text = Share(models[0]);
        if (models.Count == 1)
        {
            var savedUsd = GeminiActivityReader.EstimateCacheSavingsUsd(today);
            GeminiPrimaryModelDetail.Text = savedUsd > 0m
                ? $"缓存 {today.CacheRatioPercent:N0}% · 省 ${savedUsd:N2}"
                : $"{today.Events:N0} 次请求 · 缓存命中 {today.CacheRatioPercent:N1}%";
        }
        else
        {
            GeminiPrimaryModelDetail.Text = string.Join(" · ", models.Skip(1).Take(3).Select(Share));
        }
    }

    private void ApplyGeminiSessionSummary(GeminiRangeActivityStats stats)
    {
        var totalSessions = stats.MainSessions + stats.SubagentSessions;
        GeminiSessionStatsValue.Text = totalSessions == 0
            ? "0 会话"
            : stats.SubagentSessions > 0
                ? $"{stats.MainSessions:N0} 主 · {stats.SubagentSessions:N0} 子代理"
                : $"{stats.MainSessions:N0} 主会话";

        var desktopAgyEvents = Math.Max(0, stats.AntigravityEvents - stats.T3Events);
        string surface;
        if (stats.AntigravityEvents == 0 && stats.CliEvents == 0)
        {
            surface = "当前范围无活跃会话";
        }
        else if (stats.AntigravityEvents > 0 && stats.CliEvents == 0)
        {
            var hostLabel = desktopAgyEvents > 0 && stats.T3Events > 0
                ? $"AGY {desktopAgyEvents:N0} / T3 {stats.T3Events:N0}"
                : stats.T3Events > 0
                    ? "T3"
                    : "AGY";
            surface = $"{hostLabel} · 工具 {stats.ToolCalls:N0} 次";
        }
        else if (stats.AntigravityEvents == 0)
        {
            surface = $"CLI · {stats.CliEvents:N0} 条";
        }
        else
        {
            var parts = new List<string>(3);
            if (desktopAgyEvents > 0) parts.Add($"AGY {desktopAgyEvents:N0}");
            if (stats.T3Events > 0) parts.Add($"T3 {stats.T3Events:N0}");
            parts.Add($"CLI {stats.CliEvents:N0}");
            surface = string.Join(" / ", parts);
        }

        GeminiSessionStatsDetail.Text = surface;
    }

    private static string ShortGeminiModel(string modelId)
    {
        var key = CodexModelCost.FamilyKey(modelId);
        return key switch
        {
            "gemini-4-carbon" => "4 Carbon",
            "gemini-4-argon" => "4 Argon",
            "gemini-4-neon" => "4 Neon",
            "gemini-4-hydrogen" => "4 Hydrogen",
            "gemini-3.8-flash" => "3.8 Flash",
            "gemini-3.5-flash-lite" => "3.5 Lite",
            "gemini-3.1-pro" => "3.1 Pro",
            "gemini-3-flash" => "3 Flash",
            "gemini-2.5-pro" => "2.5 Pro",
            "gemini-2.5-flash" => "2.5 Flash",
            "gemini-2.5-flash-lite" => "2.5 Lite",
            _ => modelId.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase)
                ? modelId["gemini-".Length..]
                : modelId
        };
    }

    private async void GeminiQuotaButton_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentModule() is not GeminiUsageModule module) return;
        var dialog = new GeminiQuotaWindow(
            () => module.CurrentActivity,
            () => module.QuotaReadings,
            () => RefreshQuotaSummaryAsync(),
            runtime)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true || dialog.OpenCycle is not { } selected || isClosed) return;
        await RunUiActionAsync(async () =>
        {
            SelectGeminiCycle(module, dialog.Cycles, selected);
            await RefreshUsageAsync();
        });
    }

    private void SelectGeminiCycle(
        GeminiUsageModule module,
        IReadOnlyList<GeminiQuotaCycleUsage> cycles,
        GeminiQuotaCycleUsage selected)
    {
        module.QuotaCycles = cycles.Select(cycle => cycle.ToCodexCycle()).ToArray();
        module.SelectedCycle = module.QuotaCycles.First(cycle =>
            cycle.PeriodStart == selected.Start && cycle.PeriodEnd == selected.End);
        module.CustomStartLocal = null;
        module.Mode = RangeMode.Cycle;
        SyncRangeModeItems(module);
        UpdateRangeControls();
    }

    private void AdvanceGeminiCycle(GeminiUsageModule module, GeminiQuotaWindowUsage? window)
    {
        if (window is null || window.ReportedUsedPercent is null) return;
        var reset = GeminiQuotaCycleHistory.NormalizeReset(window.ResetLocal);
        if (module.QuotaCycles.Count == 0)
        {
            var observations = GeminiQuotaCycleHistory.ReadWeekObservations(BeijingClock.Now);
            if (observations.Count == 0) return;
            var built = GeminiQuotaCycleHistory.Build(BeijingClock.Now, observations, module.QuotaReadings, [], complete: true);
            if (built.Count == 0) return;
            module.QuotaCycles = built.Select(c => c.ToCodexCycle()).ToArray();
            module.SelectedCycle = module.QuotaCycles.FirstOrDefault(c => c.IsCurrent) ?? module.QuotaCycles[0];
            SyncRangeModeItems(module);
            return;
        }

        var head = module.QuotaCycles[0];
        var startedNew = reset > head.ResetAt.AddMinutes(10) || window.StartLocal > head.PeriodStart.AddMinutes(2);
        if (!startedNew) return;

        var followCurrent = module.SelectedCycle?.IsCurrent == true;
        var closedHead = head with
        {
            PeriodEnd = window.StartLocal > head.PeriodStart && window.StartLocal < head.PeriodEnd ? window.StartLocal : head.PeriodEnd,
            IsCurrent = false
        };
        var current = new CodexQuotaCycle(window.StartLocal, reset, reset, 1, (decimal)window.ReportedUsedPercent.Value, true);
        module.QuotaCycles = new[] { current, closedHead }
            .Concat(module.QuotaCycles.Skip(1).Select(cycle => cycle with { IsCurrent = false }))
            .ToArray();
        if (followCurrent) module.SelectedCycle = current;
        SyncRangeModeItems(module);
        if (module.Mode != RangeMode.Cycle) return;
        UpdateRangeControls();
        if (followCurrent) _ = RefreshUsageAsync(isAutomaticRefresh: true);
    }
}
