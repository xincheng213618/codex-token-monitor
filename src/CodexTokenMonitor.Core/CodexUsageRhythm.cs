namespace CodexTokenMonitor;

internal sealed record CodexSilence(DateTimeOffset Start, DateTimeOffset End)
{
    public double Hours => (End - Start).TotalHours;
}

internal sealed record CodexRhythmDay(
    DateOnly Date, int? Messages, int? ClockHours, double? InteractionHours, CodexSilence? LongestSilence);

internal sealed record CodexRhythmMonth(
    DateOnly Month, int Days, double DailyMessages, double ClockHours, double InteractionHours);

internal sealed record CodexRhythmWeek(
    DateOnly Monday, int Samples, TimeSpan WindowStart, TimeSpan WindowEnd, int WindowCount);

internal sealed record CodexUsageRhythm(
    CodexRhythmDay[] Days, CodexRhythmMonth[] Months, CodexRhythmWeek[] Weeks, CodexSilence[] LongSilences,
    int SilenceSamples, double? MedianSilenceHours, int SilencesUnderSix, int SilencesUnderFive)
{
    public static CodexUsageRhythm Calculate(CodexMessageHistory history, TimeSpan offset)
    {
        var stamps = history.Messages.Select(stamp => stamp.ToOffset(offset)).ToArray();
        var days = new List<CodexRhythmDay>();
        var longSilences = new List<CodexSilence>();
        if (stamps.Length > 0)
        {
            // End days can be partial, even when the last record predates today.
            var firstDay = Day(stamps[0]).AddDays(1);
            var lastDay = Day(stamps[^1]).AddDays(-1);
            var dailyMessages = stamps.ToLookup(Day);
            var turnDays = history.TurnStarts.Select(stamp => Day(stamp.ToOffset(offset))).ToHashSet();
            var minutes = new Dictionary<DateOnly, double>();
            var longest = new Dictionary<DateOnly, CodexSilence>();
            for (var i = 1; i < stamps.Length; i++)
            {
                var left = stamps[i - 1];
                var right = stamps[i];
                var day = Day(left);
                var endDay = Day(right);
                var gap = right - left;
                if (gap <= TimeSpan.FromMinutes(30))
                {
                    if (endDay == day)
                        minutes[day] = minutes.GetValueOrDefault(day) + gap.TotalMinutes;
                    else
                    {
                        var midnight = new DateTimeOffset(right.Date, offset);
                        minutes[day] = minutes.GetValueOrDefault(day) + (midnight - left).TotalMinutes;
                        minutes[endDay] = minutes.GetValueOrDefault(endDay) + (right - midnight).TotalMinutes;
                    }
                }

                if (day < firstDay || day > lastDay) continue;
                // Endpoints are observed. Only intervening calendar days can be missing.
                var next = day.AddDays(1);
                while (next < endDay && turnDays.Contains(next)) next = next.AddDays(1);
                if (next < endDay) continue;
                var silence = new CodexSilence(left, right);
                if (gap > TimeSpan.FromDays(1)) longSilences.Add(silence);
                if (!longest.TryGetValue(day, out var previous) || silence.Hours > previous.Hours)
                    longest[day] = silence;
            }

            for (var date = firstDay; date <= lastDay; date = date.AddDays(1))
            {
                var count = dailyMessages[date].Count();
                days.Add(count == 0 && !turnDays.Contains(date)
                    ? new(date, null, null, null, null)
                    : new(date, count, dailyMessages[date].Select(stamp => stamp.Hour).Distinct().Count(),
                        minutes.GetValueOrDefault(date) / 60, longest.GetValueOrDefault(date)));
            }
        }

        var months = days.Where(day => day.Messages.HasValue)
            .GroupBy(day => new DateOnly(day.Date.Year, day.Date.Month, 1))
            .Select(group => new CodexRhythmMonth(group.Key, group.Count(),
                Median(group.Select(day => (double)day.Messages!.Value)),
                Median(group.Select(day => (double)day.ClockHours!.Value)),
                Median(group.Select(day => day.InteractionHours!.Value))))
            .ToArray();
        var silences = days.Select(day => day.LongestSilence).OfType<CodexSilence>().ToArray();
        var weeks = silences.GroupBy(silence => Monday(Day(silence.Start)))
            .Select(group => WeeklyWindow(group.Key, group.Select(silence => silence.Start.TimeOfDay))).ToArray();
        return new(days.ToArray(), months, weeks, longSilences.ToArray(), silences.Length,
            silences.Length == 0 ? null : Median(silences.Select(silence => silence.Hours)),
            silences.Count(silence => silence.Hours < 6), silences.Count(silence => silence.Hours < 5));
    }

    private static CodexRhythmWeek WeeklyWindow(DateOnly week, IEnumerable<TimeSpan> starts)
    {
        var clocks = starts.Select(start => start.Ticks).Order().ToArray();
        var wrapped = clocks.Concat(clocks.Select(clock => clock + TimeSpan.TicksPerDay)).ToArray();
        var bestStart = 0;
        var bestCount = 1;
        long bestSpan = 0;
        for (var start = 0; start < clocks.Length; start++)
        {
            var end = start;
            while (end + 1 < start + clocks.Length && wrapped[end + 1] - wrapped[start] <= TimeSpan.TicksPerHour * 6)
                end++;
            var count = end - start + 1;
            var span = wrapped[end] - wrapped[start];
            if (count < bestCount || count == bestCount && span >= bestSpan) continue;
            bestStart = start;
            bestCount = count;
            bestSpan = span;
        }
        return new(week, clocks.Length, TimeSpan.FromTicks(wrapped[bestStart]),
            TimeSpan.FromTicks((wrapped[bestStart] + bestSpan) % TimeSpan.TicksPerDay), bestCount);
    }

    private static DateOnly Day(DateTimeOffset stamp) => DateOnly.FromDateTime(stamp.DateTime);
    private static DateOnly Monday(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return (sorted[(sorted.Length - 1) / 2] + sorted[sorted.Length / 2]) / 2;
    }
}
