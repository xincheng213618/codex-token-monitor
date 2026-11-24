namespace CodexTokenMonitor;

internal sealed record CodexRhythmDailyUsage(CodexRhythmDay Day, long? TotalTokens, DateTimeOffset? SnapshotThrough = null)
{
    public DateOnly Date => Day.Date;
    public string DateLabel => SnapshotThrough is { } cutoff ? $"今日 {cutoff:HH:mm:ss}" : $"{Date:yyyy-MM-dd}";
    public string DateDetails => SnapshotThrough is { } cutoff
        ? $"{Date:yyyy-MM-dd} · 截至 {cutoff:HH:mm:ss} · {DayTimeZoneSettings.Format(cutoff.Offset)}"
        : DateLabel;
    public int? Messages => Day.Messages;
    public int? ClockHours => Day.ClockHours;
    public double? InteractionHours => Day.InteractionHours;
    public CodexSilence? LongestSilence => Day.LongestSilence;

    public double? TokensPerMessage =>
        Messages is { } messages && messages > 0 && TotalTokens is { } total
            ? (double)total / messages
            : null;

    internal static CodexRhythmDailyUsage[] Create(
        IReadOnlyList<CodexRhythmDay> days, IReadOnlyDictionary<DateOnly, long> tokensByDay) =>
        days.Select(day => new CodexRhythmDailyUsage(day,
            tokensByDay.TryGetValue(day.Date, out var total) ? total : null)).ToArray();
}
