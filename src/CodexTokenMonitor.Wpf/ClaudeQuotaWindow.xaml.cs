using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace CodexTokenMonitor;

internal partial class ClaudeQuotaWindow : Window
{
    private readonly Func<ClaudeCodeActivitySnapshot?> activity;
    private readonly Func<IReadOnlyList<ClaudeQuotaReading>> readings;
    private readonly Action<ClaudeFableResetReminder> saveReminder;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private CancellationTokenSource? historyLoad;

    internal IReadOnlyList<ClaudeQuotaCycleUsage> Cycles { get; private set; } = [];
    internal ClaudeQuotaCycleUsage? OpenCycle { get; private set; }

    public ClaudeQuotaWindow(Func<ClaudeCodeActivitySnapshot?> activity, Func<IReadOnlyList<ClaudeQuotaReading>> readings,
        ClaudeFableResetReminder? reminder, Action<ClaudeFableResetReminder> saveReminder)
    {
        InitializeComponent();
        this.activity = activity;
        this.readings = readings;
        this.saveReminder = saveReminder;
        if (reminder is not null)
        {
            FableResetCount.Text = reminder.Remaining.ToString(CultureInfo.InvariantCulture);
            FableResetExpiry.Text = reminder.ExpiresAt.ToOffset(CodexUsageReader.BeijingOffset)
                .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }
        FableResetStatus.Text = reminder is null
            ? "首次登记请填写剩余次数与到期时间，日期格式为 yyyy-MM-dd HH:mm。"
            : $"本地记录 · {reminder.Status(BeijingClock.Now)}";
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
            var cycles = await Task.Run(() => ClaudeQuotaCycleHistory.Read(BeijingClock.Now, cancellation.Token), cancellation.Token);
            if (cancellation.IsCancellationRequested) return;
            Cycles = cycles;
            var rows = cycles.Select(cycle => new CycleRow(cycle)).ToArray();
            CycleHistoryGrid.ItemsSource = rows;
            CycleHistoryGrid.SelectedItem = rows.FirstOrDefault(row => row.Cycle.Reset == selected) ?? rows.FirstOrDefault();
            CycleHistoryStatus.Text = rows.Length == 0
                ? "暂无可用于划分周周期的官方重置时间。"
                : $"{rows.Length:N0} 个周期 · 标有「样本」的是旧版保存的官方读数";
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

    private sealed record CycleRow(ClaudeQuotaCycleUsage Cycle)
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
        public string WeekUsed => Cycle.Official is { } official
            ? $"{official.UsedPercent:0.#}% / {100 - official.UsedPercent:0.#}%" : "—";
        public string FableUsed => Cycle.Official?.FableUsedPercent is { } percent ? $"{percent:0.#}%" : "—";
        public string ObservedAt => Cycle.Official is { } official
            ? $"{official.ObservedAt:MM-dd HH:mm}" + (Cycle.OfficialIsSample ? " 样本" : "") : "—";
        public string FullReference => Cycle.FullReferenceUsd is { } amount ? "≈ " + FormatUsd(amount) : "—";
    }

    private void Refresh()
    {
        var snapshot = activity();
        var samples = readings();
        Apply(ClaudeQuotaWindowKind.FiveHour, snapshot?.FiveHourWindow,
            FiveHourTotalValue, FiveHourTokensValue, FiveHourQuotaDetail, FiveHourSampleDetail);
        Apply(ClaudeQuotaWindowKind.Week, snapshot?.WeekWindow,
            WeekTotalValue, WeekTokensValue, WeekQuotaDetail, WeekSampleDetail);
        FableQuotaText.Text = snapshot?.FableWeek is { } fable
            ? $"官方已用 {fable.UsedPercent:0.#}% · 剩余 {100 - fable.UsedPercent:0.#}% · {fable.ResetLocal:MM-dd HH:mm} 重置（UTC+8）"
            : "等待官方返回 Fable 周额度。";

        void Apply(ClaudeQuotaWindowKind kind, ClaudeQuotaWindowUsage? window,
            TextBlock total, TextBlock tokens, TextBlock quota, TextBlock detail)
        {
            var reference = ClaudeQuotaReferenceCalculator.Estimate(kind, window, samples);
            quota.Text = window?.OfficialPercent is { } percent
                ? $"官方已用 {percent:0.#}% · 剩余 {100 - percent:0.#}%\n{window.ResetLocal:MM-dd HH:mm} 重置"
                : "等待官方额度";
            if (reference is null)
            {
                total.Text = "待估算";
                tokens.Text = "-- Token";
                detail.Text = "有官方已用比例和本机 Token 后自动显示。";
                return;
            }
            total.Text = "≈ " + FormatUsd(reference.TotalCostUsd);
            tokens.Text = "≈ " + UsageDisplayFormatting.TokenAdaptive(reference.TotalTokens) + " Token";
            detail.Text = $"样本 {reference.UsedPercent:0.#}%：{FormatUsd(reference.RecordedCostUsd)} · {UsageDisplayFormatting.TokenAdaptive(reference.RecordedTokens)} Token\n" +
                $"{reference.RecordedLocal:MM-dd HH:mm} · " +
                (!reference.IsCurrentWindow ? "最近一次自动读数" : reference.IsTentative ? "样本较少，大致参考" : "按本窗口用量外推");
        }
    }

    private void SaveFableReset_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(FableResetCount.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var count) ||
            !DateTime.TryParseExact(FableResetExpiry.Text.Trim(), "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var expires) || expires.Year < 2000)
        {
            FableResetStatus.Text = "请填写非负整数和到期时间，日期格式为 yyyy-MM-dd HH:mm。";
            return;
        }
        var reminder = new ClaudeFableResetReminder(count, new DateTimeOffset(expires, CodexUsageReader.BeijingOffset));
        try
        {
            reminder.Save();
            saveReminder(reminder);
            FableResetStatus.Text = $"本地记录已保存 · {reminder.Status(BeijingClock.Now)}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FableResetStatus.Text = $"保存失败 · {ex.Message}";
        }
    }

    internal static string FormatUsd(decimal value) => value >= 10 ? $"${value:N0}" : $"${value:N2}";
}
