using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace CodexTokenMonitor;

internal partial class ClaudeQuotaWindow : Window
{
    private readonly Func<ClaudeCodeActivitySnapshot?> activity;
    private readonly Func<IReadOnlyList<ClaudeQuotaReading>> readings;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(5) };

    public ClaudeQuotaWindow(Func<ClaudeCodeActivitySnapshot?> activity, Func<IReadOnlyList<ClaudeQuotaReading>> readings)
    {
        InitializeComponent();
        this.activity = activity;
        this.readings = readings;
        timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) => timer.Start();
        Closed += (_, _) => timer.Stop();
        Refresh();
    }

    private void Refresh()
    {
        var snapshot = activity();
        var samples = readings();
        Apply(ClaudeQuotaWindowKind.FiveHour, snapshot?.FiveHourWindow,
            FiveHourTotalValue, FiveHourTokensValue, FiveHourQuotaDetail, FiveHourSampleDetail);
        Apply(ClaudeQuotaWindowKind.Week, snapshot?.WeekWindow,
            WeekTotalValue, WeekTokensValue, WeekQuotaDetail, WeekSampleDetail);

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

    internal static string FormatUsd(decimal value) => value >= 10 ? $"${value:N0}" : $"${value:N2}";
}
