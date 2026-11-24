using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace CodexTokenMonitor;

internal partial class GeminiQuotaWindow : Window
{
    private static readonly Regex DurationPartRegex = new(@"(\d+(?:\.\d+)?)\s*(d|day|days|天|h|hr|hrs|hour|hours|小时|时|m|min|mins|minute|minutes|分|分钟)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly Func<GeminiActivitySnapshot?> activity;
    private readonly Func<IReadOnlyList<ClaudeQuotaReading>> readings;
    private readonly Func<Task> onReadingSaved;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private CancellationTokenSource? historyLoad;

    internal IReadOnlyList<GeminiQuotaCycleUsage> Cycles { get; private set; } = [];
    internal GeminiQuotaCycleUsage? OpenCycle { get; private set; }

    public GeminiQuotaWindow(
        Func<GeminiActivitySnapshot?> activity,
        Func<IReadOnlyList<ClaudeQuotaReading>> readings,
        Func<Task> onReadingSaved)
    {
        InitializeComponent();
        this.activity = activity;
        this.readings = readings;
        this.onReadingSaved = onReadingSaved;

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
            WeekPercentBox.Text = (100 - p7).ToString("0.#", CultureInfo.InvariantCulture);
            WeekResetBox.Text = week.ResetLocal.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }
    }

    private async void SyncDesktop_Click(object sender, RoutedEventArgs e)
    {
        SyncDesktopButton.IsEnabled = false;
        try
        {
            var snapshot = await Task.Run(() => GeminiDesktopQuotaReader.Synchronize(force: true));
            if (snapshot is null)
            {
                ReadingSaveStatus.Text = "请先在 Antigravity 展开 View Usage，取得当前周期的读数和重置倒计时。";
                return;
            }

            await onReadingSaved();
            Refresh();
            await LoadHistoryAsync();
            ReadingSaveStatus.Text = (snapshot.FiveHour, snapshot.SevenDay) switch
            {
                (not null, not null) => "已读取 Antigravity 桌面 5h 和 7d 额度。",
                (not null, null) => "已读取桌面 5h 额度。请展开 View Usage 取得周重置倒计时。",
                _ => "已读取桌面 7d 额度。5h 等待包含重置时间的读数。"
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
            ReadingSaveStatus.Text = "请填写未来的重置时间（如 2026-11-28 08:00、14:30）或剩余倒计时（如 3h20m、4d12h）。";
            return;
        }

        var usedPercent = inputIsRemaining ? Math.Clamp(100.0 - rawPct, 0.0, 100.0) : Math.Clamp(rawPct, 0.0, 100.0);
        try
        {
            var reading = await Task.Run(() =>
                GeminiQuotaEstimator.ReadWindow(kind, usedPercent, resetLocal, now, ClaudeQuotaReadingSource.Manual));
            GeminiQuotaReadingStore.Add(reading);

            var window = new GeminiRateLimitWindow(usedPercent, resetLocal, now, ClaudeQuotaReadingSource.Manual);
            var snapshot = kind == ClaudeQuotaWindowKind.FiveHour
                ? new GeminiRateLimitSnapshot(now, window, null)
                : new GeminiRateLimitSnapshot(now, null, window);
            GeminiRateLimitLog.SaveCurrent(snapshot);

            await onReadingSaved();
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

        // Time-of-day only ("14:30") -> today at 14:30 if still ahead, else tomorrow at 14:30.
        if (TimeSpan.TryParseExact(trimmed, [@"hh\:mm", @"h\:mm", @"hh\:mm\:ss"], CultureInfo.InvariantCulture, out var tod) &&
            tod >= TimeSpan.Zero && tod < TimeSpan.FromHours(24))
        {
            var candidate = new DateTimeOffset(nowLocal.Date + tod, CodexUsageReader.BeijingOffset);
            if (candidate <= nowLocal) candidate = candidate.AddDays(1);
            resetLocal = candidate;
            return true;
        }

        // Relative countdown ("4d12h", "3h 20m", "4.5h", "2天5小时").
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

    private async void RefreshHistory_Click(object sender, RoutedEventArgs e) => await LoadHistoryAsync();

    private async Task LoadHistoryAsync()
    {
        if (historyLoad is not null) return;
        using var cancellation = new CancellationTokenSource();
        historyLoad = cancellation;
        var selected = (CycleHistoryGrid.SelectedItem as CycleRow)?.Cycle.Reset;
        RefreshHistoryButton.IsEnabled = false;
        CycleHistoryStatus.Text = "正在读取本机历史…";
        try
        {
            var cycles = await Task.Run(() => GeminiQuotaCycleHistory.Read(BeijingClock.Now, cancellation.Token), cancellation.Token);
            if (cancellation.IsCancellationRequested) return;
            Cycles = cycles;
            var rows = cycles.Select(cycle => new CycleRow(cycle)).ToArray();
            CycleHistoryGrid.ItemsSource = rows;
            CycleHistoryGrid.SelectedItem = rows.FirstOrDefault(row => row.Cycle.Reset == selected) ?? rows.FirstOrDefault();
            CycleHistoryStatus.Text = rows.Length == 0
                ? "保存一次 7d 重置时间读数（或通过 agy statusLine 同步）后，将在此按 7d 周期汇总历史用量。"
                : $"共 {rows.Length:N0} 个 7d 周期 · 可选中任一周期回到主界面查看明细";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            if (!cancellation.IsCancellationRequested) CycleHistoryStatus.Text = $"历史读取失败 · {ex.Message}";
        }
        finally
        {
            historyLoad = null;
            if (!cancellation.IsCancellationRequested) RefreshHistoryButton.IsEnabled = true;
        }
    }

    private void CycleHistoryGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        OpenCycleButton.IsEnabled = CycleHistoryGrid.SelectedItem is CycleRow;

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
        ApplyWindow(ClaudeQuotaWindowKind.FiveHour, snapshot?.FiveHourWindow, samples,
            FiveHourTotalValue, FiveHourTokensValue, FiveHourQuotaDetail, FiveHourSampleDetail);
        ApplyWindow(ClaudeQuotaWindowKind.Week, snapshot?.WeekWindow, samples,
            WeekTotalValue, WeekTokensValue, WeekQuotaDetail, WeekSampleDetail);
    }

    private static void ApplyWindow(
        ClaudeQuotaWindowKind kind,
        GeminiQuotaWindowUsage? window,
        IReadOnlyList<ClaudeQuotaReading> samples,
        TextBlock total,
        TextBlock tokens,
        TextBlock quota,
        TextBlock detail)
    {
        var reference = GeminiQuotaReferenceCalculator.Estimate(kind, window, samples);
        if (window?.ReportedUsedPercent is { } percent)
        {
            var windowCost = GeminiQuotaEstimator.UsdCost(window.GeminiUsage);
            quota.Text = $"已用 {percent:0.#}% · 剩余 {100 - percent:0.#}%（{window.ReadingLabel}）\n" +
                $"{window.ResetLabel} 重置 · 读数 {window.ObservedLocal:MM-dd HH:mm}" +
                (windowCost > 0 ? $" · 本机 API 等价 ${windowCost:N2}" : "");
        }
        else if (window is not null && window.GeminiUsage.Events > 0)
        {
            var windowCost = GeminiQuotaEstimator.UsdCost(window.GeminiUsage);
            quota.Text = $"尚未登记百分比 · 推断 {window.ResetLocal:MM-dd HH:mm} 重置\n本窗口已消耗 ${windowCost:N2}（{UsageDisplayFormatting.TokenAdaptive(window.GeminiUsage.TotalTokens)} Token）";
        }
        else
        {
            quota.Text = "等待读取桌面额度、CLI 同步或手动登记";
        }

        if (reference is null)
        {
            total.Text = "待估算";
            tokens.Text = "-- Token";
            detail.Text = "有已用百分比（≥1%）及本机 Token 后自动外推满额容量。";
            return;
        }

        total.Text = reference.TotalCostUsd > 0 ? "≈ " + ClaudeQuotaWindow.FormatUsd(reference.TotalCostUsd) : "暂无 API 等价";
        tokens.Text = "≈ " + UsageDisplayFormatting.TokenAdaptive(reference.TotalTokens) + " Token";
        var sampleCost = reference.RecordedCostUsd > 0 ? ClaudeQuotaWindow.FormatUsd(reference.RecordedCostUsd) + " · " : "";
        var basis = !reference.IsCurrentWindow ? "最近一次历史周期参考" :
            reference.UsedPercent < GeminiQuotaEstimator.ReliablePercent ? "低占比大致参考" :
            window?.IsComplete == false ? "按已取得的本机用量外推" :
            window?.ResetPrecisionSeconds is not null ? "按近似重置时间外推" : "按本窗口用量外推";
        detail.Text = $"样本 {reference.UsedPercent:0.#}%：{sampleCost}{UsageDisplayFormatting.TokenAdaptive(reference.RecordedTokens)} Token\n" +
            $"{reference.RecordedLocal:MM-dd HH:mm} · {basis}";
    }

    private sealed record CycleRow(GeminiQuotaCycleUsage Cycle)
    {
        public string Period => $"{Cycle.Start:yyyy-MM-dd HH:mm} – {Cycle.Reset:MM-dd HH:mm}";
        public string State => Cycle.IsCurrent ? "进行中" :
            Cycle.UsageIsPartial ? "用量不全" : Cycle.Tokens is null ? "缺用量记录" : "已结束";
        public string UsageDetail => Cycle.UsageThrough is { } at
            ? $"本机用量截至 {at:yyyy-MM-dd HH:mm}" + (Cycle.UsageIsPartial ? " · 仅有部分记录" : "")
            : "该周期缺少本机用量记录";
        public string Requests => Cycle.Requests is { } count ? $"{count:N0}" : "—";
        public string Tokens => Cycle.Tokens is { } value ? UsageDisplayFormatting.TokenAdaptive(value) : "—";
        public string Cost => Cycle.ApiCostUsd is { } cost
            ? (Cycle.UnpricedTokens > 0 ? "≥ " : "") + $"${cost:N2}" : "—";
        public string WeekUsed => Cycle.Observation is { } obs
            ? $"{obs.UsedPercent:0.#}% / {100 - obs.UsedPercent:0.#}%" : "—";
        public string ObservedAt => Cycle.Observation is { } obs
            ? $"{obs.ObservedAt:MM-dd HH:mm}" + (obs.Source == ClaudeQuotaReadingSource.Manual ? " 登记" : "") : "—";
        public string FullReference => Cycle.FullReferenceUsd is { } amount
            ? "≈ " + ClaudeQuotaWindow.FormatUsd(amount) : "—";
    }
}
