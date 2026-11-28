using System.Globalization;
using System.Text.RegularExpressions;

namespace CodexTokenMonitor;

internal static class GeminiDesktopQuotaParser
{
    public static IReadOnlyList<string> GroupNames { get; } = ["Gemini Models", "Gemini 模型", "Gemini模型"];

    private static readonly Regex RemainingPercent = new(
        @"^(?:剩余\s*)?(?<value>[0-9]+(?:[.,][0-9]+)?)\s*[%％](?:\s*(?:left|remaining|剩余))?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ResetLabel = new(
        @"\b(?:resets?|refresh(?:es)?)\s+in\s+|(?:重置倒计时|距离重置|剩余时间)[\s:：]*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex DurationPart = new(
        @"\G[\s,，]*(?<value>[0-9]+)\s*(?<unit>days?|d|天|hours?|hrs?|h|小时|时|minutes?|mins?|m|分钟|分|seconds?|secs?|s|秒)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex FiveHourMention = new(
        @"\b5[-\s]?hours?\b|\b5h\b|5\s*小时", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static GeminiRateLimitSnapshot? Parse(
        IEnumerable<string> names, DateTimeOffset now, GeminiRateLimitSnapshot? previous)
    {
        var labels = names.Select(name => name.Trim()).Where(name => name.Length > 0).ToArray();
        var group = Array.FindIndex(labels, name => GroupNames.Contains(name, StringComparer.OrdinalIgnoreCase));
        if (group < 0) return null;

        var end = Array.FindIndex(labels, group + 1, IsOtherGroup);
        if (end < 0) end = labels.Length;
        GeminiRateLimitWindow? five = null;
        GeminiRateLimitWindow? week = null;
        for (var i = group + 1; i < end; i++)
        {
            if (GetKind(labels[i]) is not { } kind) continue;
            var next = i + 1;
            while (next < end && GetKind(labels[next]) is null) next++;
            var section = labels.AsSpan(i + 1, next - i - 1);
            var fromUsageMenu = HasLiveCountdown(labels[i]);
            if (kind == ClaudeQuotaWindowKind.FiveHour)
                five ??= ReadWindow(section, kind, now, previous?.FiveHour, fromUsageMenu);
            else
                week ??= ReadWindow(section, kind, now, previous?.SevenDay, fromUsageMenu);
            i = next - 1;
        }

        return five is null && week is null ? null : new GeminiRateLimitSnapshot(now, five, week);
    }

    private static bool IsOtherGroup(string name) =>
        name is "Argon" or "AI Credits" ||
        name.Contains("Claude", StringComparison.OrdinalIgnoreCase) &&
        name.Contains("GPT", StringComparison.OrdinalIgnoreCase);

    private static ClaudeQuotaWindowKind? GetKind(string label) => label.ToLowerInvariant() switch
    {
        "5h limit" or "five hour limit remaining" or "5 小时剩余额度" or "5 小时额度" or "5 小时限制"
            => ClaudeQuotaWindowKind.FiveHour,
        "weekly limit" or "weekly limit remaining" or "每周剩余额度" or "每周额度" or "每周限制"
            => ClaudeQuotaWindowKind.Week,
        _ => null
    };

    private static bool HasLiveCountdown(string label) => label.ToLowerInvariant() is
        "5h limit" or "weekly limit" or "5 小时额度" or "5 小时限制" or "每周额度" or "每周限制";

    private static GeminiRateLimitWindow? ReadWindow(
        ReadOnlySpan<string> section, ClaudeQuotaWindowKind kind, DateTimeOffset now,
        GeminiRateLimitWindow? previous, bool fromUsageMenu)
    {
        double? remaining = null;
        (TimeSpan Duration, int PrecisionSeconds)? countdown = null;
        for (var i = 0; i < section.Length; i++)
        {
            var label = section[i];
            var percent = RemainingPercent.Match(label);
            if (!percent.Success && i + 1 < section.Length && section[i + 1].StartsWith('%'))
                percent = RemainingPercent.Match(label + section[i + 1]);
            if (percent.Success && double.TryParse(percent.Groups["value"].Value.Replace(',', '.'),
                    NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) && value is >= 0 and <= 100)
                remaining ??= value;

            // Settings keeps the countdown text from its last quota refresh.
            // Only the View Usage menu can supply a new reset anchor.
            if (fromUsageMenu && (kind != ClaudeQuotaWindowKind.Week || !FiveHourMention.IsMatch(label)))
                countdown ??= ReadDuration(label);
        }

        if (remaining is null) return null;
        var used = 100 - remaining.Value;
        var length = GeminiQuotaEstimator.Length(kind);
        if (previous is { } old && (old.ResetLocal <= now || old.ResetLocal > now + length))
            previous = null;

        if (previous is { } prior && prior.UsedPercent == used && prior.Source == ClaudeQuotaReadingSource.Desktop)
            return prior;

        DateTimeOffset reset;
        int? precision;
        if (countdown is { } reading && reading.Duration <= length)
        {
            // Countdown text is rounded. Retain a compatible known reset so repeated
            // samples from the same window don't turn into new historical cycles,
            // unless used percentage dropped (an early/mid-cycle reset).
            var displayed = now + reading.Duration;
            var tolerance = TimeSpan.FromSeconds(reading.PrecisionSeconds + (previous?.ResetPrecisionSeconds ?? 0));
            if (previous is { } known && used + 0.001 >= known.UsedPercent && (known.ResetLocal - displayed).Duration() <= tolerance)
            {
                reset = known.ResetLocal;
                precision = known.ResetPrecisionSeconds;
            }
            else
            {
                reset = displayed + TimeSpan.FromSeconds(reading.PrecisionSeconds / 2.0);
                precision = reading.PrecisionSeconds;
            }
        }
        else if (previous is { } known && used >= known.UsedPercent)
        {
            reset = known.ResetLocal;
            precision = known.ResetPrecisionSeconds;
        }
        else
        {
            return null;
        }

        return new GeminiRateLimitWindow(used, reset, now, ClaudeQuotaReadingSource.Desktop, precision);
    }

    public static bool TryDetectUnanchoredWeekReset(
        IEnumerable<string> names,
        GeminiRateLimitSnapshot? previous,
        out double currentRemainingPercent)
    {
        currentRemainingPercent = 0;
        if (previous?.SevenDay is not { } knownWeek) return false;
        var labels = names.Select(name => name.Trim()).Where(name => name.Length > 0).ToArray();
        var group = Array.FindIndex(labels, name => GroupNames.Contains(name, StringComparer.OrdinalIgnoreCase));
        if (group < 0) return false;

        var end = Array.FindIndex(labels, group + 1, IsOtherGroup);
        if (end < 0) end = labels.Length;
        for (var i = group + 1; i < end; i++)
        {
            if (GetKind(labels[i]) != ClaudeQuotaWindowKind.Week) continue;
            var next = i + 1;
            while (next < end && GetKind(labels[next]) is null) next++;
            for (var j = i + 1; j < next; j++)
            {
                var match = RemainingPercent.Match(labels[j]);
                if (!match.Success && j + 1 < next && labels[j + 1].StartsWith('%'))
                    match = RemainingPercent.Match(labels[j] + labels[j + 1]);
                if (match.Success &&
                    double.TryParse(match.Groups["value"].Value.Replace(',', '.'),
                        NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var rem) &&
                    rem is >= 0 and <= 100)
                {
                    var used = 100.0 - rem;
                    if (used + 2.0 < knownWeek.UsedPercent)
                    {
                        currentRemainingPercent = rem;
                        return true;
                    }
                    return false;
                }
            }
        }

        return false;
    }

    private static (TimeSpan Duration, int PrecisionSeconds)? ReadDuration(string label)
    {
        var marker = ResetLabel.Match(label);
        if (!marker.Success) return null;

        long seconds = 0;
        var precision = 0;
        foreach (Match part in DurationPart.Matches(label[(marker.Index + marker.Length)..]))
        {
            if (!int.TryParse(part.Groups["value"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                return null;
            precision = part.Groups["unit"].Value.ToLowerInvariant()[0] switch
            {
                'd' or '天' => 86400,
                'h' or '小' or '时' => 3600,
                'm' or '分' => 60,
                _ => 1
            };
            seconds += (long)value * precision;
        }
        return precision > 0 && seconds <= TimeSpan.FromDays(7).TotalSeconds ? (TimeSpan.FromSeconds(seconds), precision) : null;
    }
}
