namespace CodexTokenMonitor;

internal sealed record CodexRhythmToday(DateTimeOffset Cutoff, CodexRhythmDay Day, long? TotalTokens)
{
    internal static CodexRhythmToday Calculate(
        CodexMessageHistory history, DateTimeOffset cutoff, TimeSpan offset, long? totalTokens)
    {
        var localCutoff = cutoff.ToOffset(offset);
        var start = new DateTimeOffset(localCutoff.Date, offset);
        var messages = history.Messages.Where(stamp => stamp >= start && stamp < cutoff).ToArray();
        var observed = messages.Length > 0 || history.TurnStarts.Any(stamp => stamp >= start && stamp < cutoff);
        var day = new CodexRhythmDay(
            DateOnly.FromDateTime(localCutoff.Date),
            observed ? messages.Length : null,
            observed ? messages.Select(stamp => stamp.ToOffset(offset).Hour).Distinct().Count() : null,
            InteractionHours: null,
            LongestSilence: null);

        return new(localCutoff, day, totalTokens);
    }
}
