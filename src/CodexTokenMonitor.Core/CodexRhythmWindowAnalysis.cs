namespace CodexTokenMonitor;

internal readonly record struct CodexRhythmTokenEvent(DateTimeOffset Timestamp, long TotalTokens);

internal sealed record CodexRhythmWindowPoint(
    int Hours, int MessageSampleDays, double? MedianMessages, double? MedianMessagesPerHour,
    int TokenSampleDays, double? MedianTokens);

internal sealed record CodexRhythmWindowAnalysis(DateOnly Start, DateOnly End, CodexRhythmWindowPoint[] Points)
{
    internal const int LookbackDays = 56;
    private static readonly int[] WindowHours = [1, 2, 4, 8, 12, 16, 24];

    internal static CodexRhythmWindowAnalysis? Calculate(
        CodexMessageHistory history, CodexUsageRhythm rhythm, TimeSpan offset,
        IReadOnlyDictionary<DateOnly, long> dailyTokens, IReadOnlyList<CodexRhythmTokenEvent> tokenEvents,
        CancellationToken token = default)
    {
        if (rhythm.Days.Length == 0) return null;

        var end = rhythm.Days[^1].Date;
        var start = DateOnly.FromDayNumber(Math.Max(rhythm.Days[0].Date.DayNumber, end.DayNumber - LookbackDays + 1));
        var days = rhythm.Days.Where(day => day.Date >= start && day.Messages.HasValue)
            .Select(day =>
            {
                var midnight = new DateTimeOffset(day.Date.ToDateTime(TimeOnly.MinValue), offset);
                return new Observation(midnight, LowerBound(history.Messages, midnight),
                    LowerBound(history.Messages, midnight.AddDays(1)),
                    dailyTokens.TryGetValue(day.Date, out var total) ? total : null);
            })
            .ToArray();
        if (days.Length == 0) return null;

        token.ThrowIfCancellationRequested();
        var sortedEvents = tokenEvents.OrderBy(item => item.Timestamp).ToArray();
        var tokenTimestamps = new DateTimeOffset[sortedEvents.Length];
        var tokenPrefix = new long[sortedEvents.Length + 1];
        for (var i = 0; i < sortedEvents.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            tokenTimestamps[i] = sortedEvents[i].Timestamp;
            tokenPrefix[i + 1] = tokenPrefix[i] + sortedEvents[i].TotalTokens;
        }

        var points = new CodexRhythmWindowPoint[WindowHours.Length];
        for (var i = 0; i < WindowHours.Length; i++)
        {
            var hours = WindowHours[i];
            var length = TimeSpan.FromHours(hours);
            var messages = new List<double>(days.Length);
            var tokens = new List<double>(days.Length);
            foreach (var day in days)
            {
                token.ThrowIfCancellationRequested();
                var stamps = history.Messages.AsSpan(day.FirstMessage, day.PastLastMessage - day.FirstMessage);
                var window = FindBusiestWindow(stamps, day.Start, length);
                messages.Add(window.Messages);
                if (day.DailyTokens is not { } dailyTotal) continue;

                if (hours == 24)
                {
                    tokens.Add(dailyTotal);
                    continue;
                }

                var firstToken = LowerBound(tokenTimestamps, window.Start);
                var pastLastToken = LowerBound(tokenTimestamps, window.Start + length);
                tokens.Add(tokenPrefix[pastLastToken] - tokenPrefix[firstToken]);
            }

            var medianMessages = Median(messages);
            points[i] = new(hours, messages.Count, medianMessages, medianMessages / hours, tokens.Count, Median(tokens));
        }

        return new(start, end, points);
    }

    private static MessageWindow FindBusiestWindow(
        ReadOnlySpan<DateTimeOffset> messages, DateTimeOffset dayStart, TimeSpan length)
    {
        var bestWindow = new MessageWindow(dayStart, 0);
        var latestStart = dayStart.AddDays(1) - length;
        var right = 0;
        for (var left = 0; left < messages.Length; left++)
        {
            var start = messages[left];
            if (start >= latestStart)
            {
                // This is the first message included by a window ending at midnight.
                if (messages.Length - left > bestWindow.Messages)
                    bestWindow = new(latestStart, messages.Length - left);
                break;
            }

            var end = start + length;
            while (right < messages.Length && messages[right] < end) right++;
            if (right - left > bestWindow.Messages)
                bestWindow = new(start, right - left);
        }

        return bestWindow;
    }

    private static int LowerBound(ReadOnlySpan<DateTimeOffset> stamps, DateTimeOffset threshold)
    {
        var low = 0;
        var high = stamps.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (stamps[middle] < threshold) low = middle + 1;
            else high = middle;
        }

        return low;
    }

    private static double? Median(List<double> values)
    {
        if (values.Count == 0) return null;
        values.Sort();
        return (values[(values.Count - 1) / 2] + values[values.Count / 2]) / 2;
    }

    private readonly record struct Observation(DateTimeOffset Start, int FirstMessage, int PastLastMessage, long? DailyTokens);
    private readonly record struct MessageWindow(DateTimeOffset Start, int Messages);
}
