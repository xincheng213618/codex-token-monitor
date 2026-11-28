using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace CodexTokenMonitor;

internal partial class GeminiQuotaWindow : Window
{
    private static readonly Regex DurationPartRegex = new(@"(\d+(?:\.\d+)?)\s*(d|day|days|天|h|hr|hrs|hour|hours|小时|时|m|min|mins|minute|minutes|分|分钟)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly Func<GeminiActivitySnapshot?> activity;
    private readonly Func<IReadOnlyList<ClaudeQuotaReading>> readings;
    private readonly Func<Task> onReadingSaved;
    private readonly MonitorRuntime? runtime;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private CancellationTokenSource? historyLoad;
    private bool isManualEstimating;

    internal IReadOnlyList<GeminiQuotaCycleUsage> Cycles { get; private set; } = [];
    internal GeminiQuotaCycleUsage? OpenCycle { get; private set; }

    public GeminiQuotaWindow(
        Func<GeminiActivitySnapshot?> activity,
        Func<IReadOnlyList<ClaudeQuotaReading>> readings,
        Func<Task> onReadingSaved,
        MonitorRuntime? runtime = null)
    {
        InitializeComponent();
        this.activity = activity;
        this.readings = readings;
        this.onReadingSaved = onReadingSaved;
        this.runtime = runtime;

        PrefillInputs();
        timer.Tick += (_, _) => Refresh();
        Loaded += async (_, _) =>
        {
            timer.Start();
            await LoadHistoryAsync();
        };
        Closed += (_, _) =>
        {
            timer.Stop();
            historyLoad?.Cancel();
        };
        Refresh();
    }

    private void PrefillInputs()
    {
        var snap = activity();
        if (snap?.FiveHourWindow is { ReportedUsedPercent: { } p5 } five)
        {
            FiveHourPercentBox.Text = (100 - p5).ToString("0.#", CultureInfo.InvariantCulture);
            FiveHourResetBox.Text = five.ResetLocal.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }

        if (snap?.WeekWindow is { ReportedUsedPercent: { } p7 } week)
        {
            var rem = Math.Clamp(100 - p7, 0, 100);
            WeekPercentBox.Text = rem.ToString("0.#", CultureInfo.InvariantCulture);
            WeekResetBox.Text = week.ResetLocal.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            if (rem < 100)
            {
                ManualFromBox.Value = 100m;
                ManualToBox.Value = Math.Round((decimal)rem, 0);
            }
        }
    }

    private async void SyncDesktop_Click(object sender, RoutedEventArgs e)
    {
        SyncDesktopButton.IsEnabled = false;
        try
        {
            var (snapshot, unanchoredRemaining) = await Task.Run(() =>
            {
                var synced = GeminiDesktopQuotaReader.Synchronize(force: true, out var rem);
                return (synced, rem);
            });
            if (snapshot is null)
            {
                ReadingSaveStatus.Text = unanchoredRemaining is { } remPct
                    ? $"检测到 Gemini 7d 额度已提前重置（当前剩余 {remPct:0.#}%）！请在 Antigravity 点开 View Usage 再点同步，以记录新重置时间。"
                    : "请先在 Antigravity 展开 View Usage 面板，取得当前周期读数与重置倒计时。";
                return;
            }

            await onReadingSaved();
            PrefillInputs();
            Refresh();
            await LoadHistoryAsync();
            ReadingSaveStatus.Text = (snapshot.FiveHour, snapshot.SevenDay) switch
            {
                (not null, not null) => "已读取 Antigravity 桌面 5h 和 7d 额度并归档历史。",
                (not null, null) => "已读取桌面 5h 额度。请展开 View Usage 取得周重置倒计时。",
                _ => "已读取桌面 7d 额度并归档历史。5h 等待包含重置时间的读数。"
            };
        }
        catch (Exception ex)
        {
            ReadingSaveStatus.Text = $"同步桌面额度失败：{ex.Message}";
        }
        finally
        {
            SyncDesktopButton.IsEnabled = true;
        }
    }

    private void CopyStatuslineConfig_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var scriptPath = InstallStatuslineScript();
            var snippet = JsonSerializer.Serialize(new
            {
                statusLine = new
                {
                    enabled = true,
                    type = "command",
                    command = $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\""
                }
            }, new JsonSerializerOptions { WriteIndented = true });
            Clipboard.SetText(snippet);
            ReadingSaveStatus.Text = "已复制 agy statusLine 配置到剪贴板，粘贴至 ~/.gemini/antigravity-cli/settings.json 即可自动同步。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException)
        {
            ReadingSaveStatus.Text = $"无法复制 agy statusLine 配置：{ex.Message}";
        }
    }

    internal static string InstallStatuslineScript()
    {
        using var resource = typeof(GeminiQuotaWindow).Assembly.GetManifestResourceStream("CodexTokenMonitor.GeminiStatusline.ps1")
            ?? throw new IOException("程序缺少 agy statusLine 脚本。");
        var directory = Path.Combine(MonitorCachePaths.LocalAppData, GeminiUsageReader.CacheFolder);
        Directory.CreateDirectory(directory);
        var scriptPath = Path.Combine(directory, "gemini-statusline.ps1");
        var temporary = Path.Combine(directory, $"gemini-statusline-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var target = File.Create(temporary)) resource.CopyTo(target);
            File.Move(temporary, scriptPath, overwrite: true);
            return scriptPath;
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private async void SaveFiveHourReading_Click(object sender, RoutedEventArgs e) =>
        await SaveReadingAsync(ClaudeQuotaWindowKind.FiveHour, FiveHourModeBox.SelectedIndex == 0, FiveHourPercentBox.Text, FiveHourResetBox.Text);

    private async void SaveWeekReading_Click(object sender, RoutedEventArgs e) =>
        await SaveReadingAsync(ClaudeQuotaWindowKind.Week, WeekModeBox.SelectedIndex == 0, WeekPercentBox.Text, WeekResetBox.Text);

    private async Task SaveReadingAsync(ClaudeQuotaWindowKind kind, bool inputIsRemaining, string percentText, string resetText)
    {
        var cleanPct = percentText.Trim().TrimEnd('%').Trim();
        if (!double.TryParse(cleanPct, NumberStyles.Float, CultureInfo.InvariantCulture, out var rawPct) ||
            !double.IsFinite(rawPct) || rawPct < 0 || rawPct > 100)
        {
            ReadingSaveStatus.Text = "请输入 0 到 100 之间的有效百分比数值。";
            return;
        }

        var now = BeijingClock.Now;
        if (!TryParseResetInput(resetText, now, kind, out var resetLocal) || resetLocal <= now)
        {
            ReadingSaveStatus.Text = "请填写未来的重置时间（如 2026-12-03 08:00、14:30）或剩余倒计时（如 3h20m、6d22h）。";
            return;
        }

        var usedPercent = inputIsRemaining ? Math.Clamp(100.0 - rawPct, 0.0, 100.0) : Math.Clamp(rawPct, 0.0, 100.0);
        try
        {
            var window = new GeminiRateLimitWindow(usedPercent, resetLocal, now, ClaudeQuotaReadingSource.Manual);
            var snapshot = kind == ClaudeQuotaWindowKind.FiveHour
                ? new GeminiRateLimitSnapshot(now, window, null)
                : new GeminiRateLimitSnapshot(now, null, window);
            GeminiRateLimitLog.SaveCurrent(snapshot);

            var reading = await Task.Run(() =>
            {
                var start = kind == ClaudeQuotaWindowKind.Week
                    ? GeminiQuotaCycleHistory.ResolveActiveWeekStart(resetLocal, usedPercent, now, now)
                    : resetLocal - GeminiQuotaEstimator.Length(kind);
                var (rows, _) = GeminiUsageReader.ReadTransientDetailRowsWithStatus(start, now.AddMinutes(1));
                var usage = GeminiQuotaEstimator.GeminiModelUsage(rows, start, now.AddTicks(1));
                return new ClaudeQuotaReading(
                    kind,
                    now,
                    usedPercent,
                    start,
                    resetLocal,
                    GeminiQuotaEstimator.UsdCost(usage),
                    usage.TotalTokens,
                    usage.Events,
                    ClaudeQuotaReadingSource.Manual);
            });
            GeminiQuotaReadingStore.Add(reading);

            await onReadingSaved();
            PrefillInputs();
            Refresh();
            await LoadHistoryAsync();

            var label = kind == ClaudeQuotaWindowKind.FiveHour ? "5h" : "7d";
            ReadingSaveStatus.Text = reading.CapacityUsd is { } cap
                ? $"已保存 {label} 读数（剩余 {100 - usedPercent:0.#}% · {resetLocal:MM-dd HH:mm} 重置）· 折算满额 ≈ {ClaudeQuotaWindow.FormatUsd(cap)}"
                : $"已保存 {label} 读数（剩余 {100 - usedPercent:0.#}% · {resetLocal:MM-dd HH:mm} 重置）";
        }
        catch (Exception ex)
        {
            ReadingSaveStatus.Text = $"保存失败：{ex.Message}";
        }
    }

    internal static bool TryParseResetInput(
        string text,
        DateTimeOffset nowLocal,
        ClaudeQuotaWindowKind kind,
        out DateTimeOffset resetLocal)
    {
        resetLocal = default;
        var trimmed = text.Trim().TrimStart('+');
        if (trimmed.Length == 0) return false;

        string[] exactFormats = ["yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy/MM/dd HH:mm", "MM-dd HH:mm", "M-d HH:mm"];
        foreach (var fmt in exactFormats)
        {
            if (DateTime.TryParseExact(trimmed, fmt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            {
                if (!fmt.StartsWith("yyyy", StringComparison.Ordinal))
                    dt = new DateTime(nowLocal.Year, dt.Month, dt.Day, dt.Hour, dt.Minute, dt.Second);
                var candidate = new DateTimeOffset(dt, CodexUsageReader.BeijingOffset);
                if (!fmt.StartsWith("yyyy", StringComparison.Ordinal) && candidate <= nowLocal)
                    candidate = candidate.AddYears(1);
                resetLocal = candidate;
                return true;
            }
        }

        if (TimeSpan.TryParseExact(trimmed, [@"hh\:mm", @"h\:mm", @"hh\:mm\:ss"], CultureInfo.InvariantCulture, out var tod) &&
            tod >= TimeSpan.Zero && tod < TimeSpan.FromHours(24))
        {
            var candidate = new DateTimeOffset(nowLocal.Date + tod, CodexUsageReader.BeijingOffset);
            if (candidate <= nowLocal) candidate = candidate.AddDays(1);
            resetLocal = candidate;
            return true;
        }

        var matches = DurationPartRegex.Matches(trimmed);
        if (matches.Count > 0)
        {
            var total = TimeSpan.Zero;
            foreach (Match m in matches)
            {
                if (!double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var val))
                    return false;
                var unit = m.Groups[2].Value.ToLowerInvariant();
                total += unit switch
                {
                    "d" or "day" or "days" or "天" => TimeSpan.FromDays(val),
                    "h" or "hr" or "hrs" or "hour" or "hours" or "小时" or "时" => TimeSpan.FromHours(val),
                    _ => TimeSpan.FromMinutes(val)
                };
            }

            if (total > TimeSpan.Zero)
            {
                resetLocal = nowLocal + total;
                return true;
            }
        }

        if (DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var general))
        {
            resetLocal = general.ToOffset(CodexUsageReader.BeijingOffset);
            return true;
        }

        return false;
    }

    private async void ManualEstimateButton_Click(object sender, RoutedEventArgs e)
    {
        if (isManualEstimating) return;
        isManualEstimating = true;
        ManualEstimateButton.IsEnabled = false;
        try
        {
            var fromRem = ManualFromBox.Value ?? 100m;
            var toRem = ManualToBox.Value ?? 73m;
            var currentWeek = activity()?.WeekWindow;
            var knownCycles = Cycles;
            var summary = await Task.Run(() =>
                GeminiManualWeekEstimator.Build(currentWeek, knownCycles, fromRem, toRem));
            ManualResultText.Text = summary;
            ManualResultText.ToolTip = summary;
        }
        catch (Exception ex)
        {
            ManualResultText.Text = $"区间估算失败：{ex.Message}";
        }
        finally
        {
            isManualEstimating = false;
            ManualEstimateButton.IsEnabled = true;
        }
    }

    private async void RefreshHistory_Click(object sender, RoutedEventArgs e) => await LoadHistoryAsync();

    private async Task LoadHistoryAsync()
    {
        if (historyLoad is not null) return;
        using var cancellation = new CancellationTokenSource();
        historyLoad = cancellation;
        var selectedStart = (CycleHistoryGrid.SelectedItem as CycleRow)?.Cycle.Start;
        RefreshHistoryButton.IsEnabled = false;
        LoadingProgress.Visibility = Visibility.Visible;
        CycleHistoryStatus.Text = "正在读取本机历史周期…";
        try
        {
            var now = BeijingClock.Now;
            var cycles = await Task.Run(() => GeminiQuotaCycleHistory.Read(now, cancellation.Token), cancellation.Token);
            if (cancellation.IsCancellationRequested) return;
            Cycles = cycles;
            var rows = cycles.Select(cycle => new CycleRow(cycle, now)).ToArray();
            CycleHistoryGrid.ItemsSource = rows;
            CycleHistoryGrid.SelectedItem = rows.FirstOrDefault(row => row.Cycle.Start == selectedStart) ?? rows.FirstOrDefault();
            HistoryCountText.Text = $"{rows.Length:N0} 个周期";
            HistoryEmptyText.Text = "保存一次 7d 重置时间读数（或展开 Antigravity View Usage 同步）后，将在此按实际 7d 周期（含提前重置）汇总历史用量。";
            CycleHistoryStatus.Text = rows.Length == 0
                ? "暂无历史周期 · 展开 Antigravity 的 View Usage 点「同步桌面额度」即可开始记录"
                : $"已加载 {rows.Length:N0}/{rows.Length:N0} 个历史周期 · 支持提前重置自动切分，双击或点「分析所选周期」查看详情";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            if (!cancellation.IsCancellationRequested)
                CycleHistoryStatus.Text = $"历史读取失败 · {ex.Message}";
        }
        finally
        {
            historyLoad = null;
            if (!cancellation.IsCancellationRequested)
            {
                RefreshHistoryButton.IsEnabled = true;
                LoadingProgress.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void CycleHistoryGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var hasSelection = CycleHistoryGrid.SelectedItem is CycleRow;
        OpenCycleButton.IsEnabled = hasSelection;
        AnalyzeCycleButton.IsEnabled = hasSelection;
    }

    private void CycleHistoryGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var current = e.OriginalSource as DependencyObject;
        while (current is not null and not DataGridRow)
        {
            current = VisualTreeHelper.GetParent(current);
        }

        if (current is DataGridRow row)
        {
            row.IsSelected = true;
            row.Focus();
        }
    }

    private void CycleHistoryGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (CycleHistoryGrid.SelectedItem is CycleRow)
        {
            AnalyzeSelectedCycle();
        }
    }

    private void AnalyzeCycle_Click(object sender, RoutedEventArgs e) => AnalyzeSelectedCycle();

    private void AnalyzeSelectedCycle()
    {
        if (CycleHistoryGrid.SelectedItem is not CycleRow row) return;
        var period = row.Cycle.ToCodexCycle();
        var prev = Cycles
            .Where(c => c.Start < row.Cycle.Start)
            .OrderByDescending(c => c.Start)
            .FirstOrDefault()
            ?.ToCodexCycle();
        var window = new QuotaCycleAnalysisWindow(
            period,
            currentWeek: null,
            previousPeriod: prev,
            runtime: runtime,
            analysisSource: new GeminiQuotaAnalysisSource(),
            cycleTitle: "Gemini 7d 周期",
            priceGroup: PricePresetGroups.Gemini)
        {
            Owner = this
        };
        window.Show();
        CycleHistoryStatus.Text = $"已打开周期分析：{row.Period}";
    }

    private void QuotaCurveButton_Click(object sender, RoutedEventArgs e)
    {
        var knownCycles = Cycles;
        var window = new QuotaCostCurveWindow(
            token => GeminiQuotaCostCurveBuilder.Build(knownCycles, cancellationToken: token),
            "Gemini 额度费用曲线",
            runtime: runtime)
        {
            Owner = this
        };
        window.Show();
    }

    private void OpenCycle_Click(object sender, RoutedEventArgs e)
    {
        if (CycleHistoryGrid.SelectedItem is not CycleRow row) return;
        OpenCycle = row.Cycle;
        DialogResult = true;
    }

    private void Refresh()
    {
        var snapshot = activity();
        var samples = readings();
        ApplyCard(ClaudeQuotaWindowKind.FiveHour, snapshot?.FiveHourWindow, samples,
            FiveHourTotalValue, FiveHourSourceTag, FiveHourQuotaDetail, FiveHourSampleDetail);
        ApplyCard(ClaudeQuotaWindowKind.Week, snapshot?.WeekWindow, samples,
            WeekTotalValue, WeekSourceTag, WeekQuotaDetail, WeekSampleDetail);
    }

    private static void ApplyCard(
        ClaudeQuotaWindowKind kind,
        GeminiQuotaWindowUsage? window,
        IReadOnlyList<ClaudeQuotaReading> samples,
        TextBlock mainValue,
        TextBlock sourceTag,
        TextBlock quotaDetail,
        TextBlock sampleDetail)
    {
        var reference = GeminiQuotaReferenceCalculator.Estimate(kind, window, samples);
        if (window?.ReportedUsedPercent is { } percent)
        {
            var remaining = Math.Max(0, 100 - percent);
            mainValue.Text = $"{remaining:0.#}%";
            sourceTag.Text = window.ReadingLabel;

            var windowCost = GeminiQuotaEstimator.UsdCost(window.GeminiUsage);
            var fullLine = reference is { TotalCostUsd: > 0 }
                ? $"满额 100% ≈ {ClaudeQuotaWindow.FormatUsd(reference.TotalCostUsd)}（≈ {UsageDisplayFormatting.TokenAdaptive(reference.TotalTokens)} Token）"
                : "等待本机 Token 记录以折算满额";

            if (kind == ClaudeQuotaWindowKind.Week)
            {
                var now = BeijingClock.Now;
                var effectiveEnd = now < window.ResetLocal ? now : window.ResetLocal;
                var tempCycle = new CodexQuotaCycle(window.StartLocal, effectiveEnd, window.ResetLocal, 1, (decimal)percent, true);
                var pace = QuotaPaceAnalyzer.FormatWeeklyCycle(
                    tempCycle,
                    (decimal)percent,
                    windowCost,
                    reference?.TotalCostUsd,
                    FormatMoney);
                quotaDetail.Text = $"标准 API 等价 ${windowCost:N2} · 已用 {percent:0.#}% / 应 {pace.ExpectedText}（{pace.RhythmText}）\n{fullLine}";
            }
            else
            {
                quotaDetail.Text = $"标准 API 等价 ${windowCost:N2} · 已用 {percent:0.#}%\n{fullLine}";
            }

            sampleDetail.Text = $"{window.StartLocal:MM-dd HH:mm}-{window.ResetLabel} 重置 · 读数 {window.ObservedLocal:MM-dd HH:mm}";
            return;
        }

        mainValue.Text = "未获取";
        sourceTag.Text = reference is not null ? "历史参考" : "";
        if (window is not null && window.GeminiUsage.Events > 0)
        {
            var windowCost = GeminiQuotaEstimator.UsdCost(window.GeminiUsage);
            quotaDetail.Text = $"当前窗口已耗 ${windowCost:N2}（{UsageDisplayFormatting.TokenAdaptive(window.GeminiUsage.TotalTokens)} Token）\n" +
                (reference is { TotalCostUsd: > 0 }
                    ? $"历史满额参考 ≈ {ClaudeQuotaWindow.FormatUsd(reference.TotalCostUsd)}（≈ {UsageDisplayFormatting.TokenAdaptive(reference.TotalTokens)} Token）"
                    : "展开 View Usage 同步百分比后可反推满额");
            sampleDetail.Text = $"推断 {window.ResetLocal:MM-dd HH:mm} 重置 · 尚未取得官方剩余%";
        }
        else
        {
            quotaDetail.Text = reference is { TotalCostUsd: > 0 }
                ? $"历史满额参考 ≈ {ClaudeQuotaWindow.FormatUsd(reference.TotalCostUsd)}（≈ {UsageDisplayFormatting.TokenAdaptive(reference.TotalTokens)} Token）\n等待当前窗口读数"
                : "等待读取桌面额度、CLI 同步或手动登记";
            sampleDetail.Text = reference is not null
                ? $"参考样本 {reference.RecordedLocal:MM-dd HH:mm}（已用 {reference.UsedPercent:0.#}%）"
                : "有已用百分比（≥1%）及本机 Token 后自动外推满额容量";
        }
    }

    private static string FormatMoney(decimal value) => value switch
    {
        >= 100 => $"${value:N0}",
        >= 10 => $"${value:N2}",
        >= 1 => $"${value:N2}",
        _ => $"${value:N4}"
    };

    private sealed class CycleRow
    {
        public CycleRow(GeminiQuotaCycleUsage cycle, DateTimeOffset now)
        {
            Cycle = cycle;
            var codexCycle = cycle.ToCodexCycle();
            var displayEnd = cycle.IsCurrent && now < cycle.End ? now : cycle.End;
            Period = $"{cycle.Start:MM-dd HH:mm} - {displayEnd:MM-dd HH:mm}";
            Duration = QuotaEstimateCalculator.FormatCycleDuration(codexCycle, now);
            ResetAt = cycle.Reset.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
            SnapshotCount = cycle.SnapshotCount > 0 ? cycle.SnapshotCount.ToString("N0", CultureInfo.InvariantCulture) : "—";

            var maxUsed = cycle.MaxUsedPercent is { } u ? (decimal)Math.Clamp(u, 0.0, 100.0) : (decimal?)null;
            Remaining = maxUsed is { } remUsed ? $"{Math.Max(0m, 100m - remUsed):0.#}%" : "—";
            UsedPercent = maxUsed is { } uPct ? $"{uPct:0.#}%" : "—";

            var paceCycle = cycle.IsCurrent ? codexCycle with { PeriodEnd = displayEnd } : codexCycle;
            var pace = QuotaPaceAnalyzer.FormatWeeklyCycle(
                paceCycle,
                maxUsed,
                cycle.ApiCostUsd ?? 0m,
                cycle.FullReferenceUsd,
                FormatMoney);
            ExpectedPercent = pace.ExpectedText;
            Rhythm = pace.RhythmText;

            Requests = cycle.Requests is { } count ? $"{count:N0}" : "—";
            Tokens = cycle.Tokens is { } value ? $"{value / 1_000_000d:N3}M" : "—";
            Cost = cycle.ApiCostUsd is { } cost
                ? (cycle.UnpricedTokens > 0 ? "≥ " : "") + FormatMoney(cost)
                : "—";
            FullReference = cycle.FullReferenceUsd is { } amount
                ? FormatMoney(amount)
                : "—";
            ObservedAt = cycle.Observation is { } obs
                ? $"{obs.ObservedAt:MM-dd HH:mm} " + (obs.Source switch
                {
                    ClaudeQuotaReadingSource.Desktop => "桌面",
                    ClaudeQuotaReadingSource.Manual => "手动",
                    _ => "CLI"
                })
                : "—";

            var stateLabel = cycle.IsCurrent
                ? "进行中"
                : (cycle.End - cycle.Start) < TimeSpan.FromDays(6.9) && cycle.SnapshotCount > 0
                    ? "提前重置结束"
                    : "自然结束";
            UsageDetail = $"{stateLabel} · 周期 {cycle.Start:yyyy-MM-dd HH:mm} → {cycle.End:yyyy-MM-dd HH:mm}（原定重置 {cycle.Reset:yyyy-MM-dd HH:mm}）" +
                (cycle.UsageThrough is { } at ? $"\n本机用量统计至 {at:yyyy-MM-dd HH:mm}" : "") +
                (cycle.UsageIsPartial ? " · 仅有部分记录" : "");
        }

        public GeminiQuotaCycleUsage Cycle { get; }
        public string Period { get; }
        public string Duration { get; }
        public string ResetAt { get; }
        public string SnapshotCount { get; }
        public string Remaining { get; }
        public string UsedPercent { get; }
        public string ExpectedPercent { get; }
        public string Rhythm { get; }
        public string Requests { get; }
        public string Tokens { get; }
        public string Cost { get; }
        public string FullReference { get; }
        public string ObservedAt { get; }
        public string UsageDetail { get; }
    }
}
