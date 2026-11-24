namespace CodexTokenMonitor;

internal sealed record CodexRhythmDailyTrendPoint(
    DateOnly Date, double? Messages, double? Tokens, double? TokensPerMessage)
{
    private const int WindowDays = 7;

    internal static CodexRhythmDailyTrendPoint[] Create(IReadOnlyList<CodexRhythmDailyUsage> days, bool movingAverage)
    {
        if (!movingAverage)
            return days.Select(day => new CodexRhythmDailyTrendPoint(
                day.Date, day.Messages, day.TotalTokens, day.TokensPerMessage)).ToArray();

        var points = new CodexRhythmDailyTrendPoint[days.Count];
        var consecutiveDays = 0;
        for (var i = 0; i < days.Count; i++)
        {
            var date = days[i].Date;
            consecutiveDays = i > 0 && date.DayNumber - days[i - 1].Date.DayNumber == 1 ? consecutiveDays + 1 : 1;
            if (consecutiveDays < WindowDays)
            {
                points[i] = new(date, null, null, null);
                continue;
            }

            // Lifted addition keeps each total unknown if its series has a gap.
            long? messageTotal = 0;
            decimal? tokenTotal = 0;
            for (var j = i - WindowDays + 1; j <= i; j++)
            {
                messageTotal += days[j].Messages;
                tokenTotal += days[j].TotalTokens;
            }

            var messages = messageTotal / (double)WindowDays;
            var tokens = (double?)(tokenTotal / WindowDays);
            var tokensPerMessage = messageTotal is > 0 ? (double?)(tokenTotal / messageTotal) : null;
            points[i] = new(date, messages, tokens, tokensPerMessage);
        }

        return points;
    }
}
