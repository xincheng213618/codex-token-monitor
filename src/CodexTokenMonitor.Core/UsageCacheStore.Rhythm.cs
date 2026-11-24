namespace CodexTokenMonitor;

internal sealed record CodexRhythmTokens(
    IReadOnlyDictionary<DateOnly, long> DailyTotals,
    IReadOnlyList<CodexRhythmTokenEvent> Events);

internal sealed partial class UsageCacheStore
{
    internal CodexRhythmTokens ReadRhythmTokens(
        CodexUsageRhythm rhythm, TimeSpan reportingOffset, CancellationToken token)
    {
        var dailyTotals = new Dictionary<DateOnly, long>();
        var events = new List<CodexRhythmTokenEvent>();
        token.ThrowIfCancellationRequested();
        if (rhythm.Days.Length == 0 || !EnsureAvailable())
            return new(dailyTotals, events);

        // Keep the day headers and event details on the same SQLite snapshot during a refresh or import.
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: true);
        using var totals = CreateRhythmTotalCommand(connection, transaction);

        using var complete = connection.CreateCommand();
        complete.Transaction = transaction;
        complete.CommandText = """
            SELECT date, total_tokens
            FROM usage_days
            WHERE date >= $first AND date <= $last AND is_complete <> 0 AND total_tokens >= 0
              AND events = (SELECT COUNT(*) FROM usage_events WHERE usage_events.date = usage_days.date)
            """;
        var first = RhythmDayStart(rhythm.Days[0].Date, reportingOffset);
        var last = RhythmDayStart(rhythm.Days[^1].Date, reportingOffset);
        var cacheOffset = CodexUsageReader.BeijingOffset;
        complete.Parameters.AddWithValue("$first", DateKey(RhythmDate(first, cacheOffset)));
        complete.Parameters.AddWithValue("$last", DateKey(RhythmDate(last.AddDays(1).AddTicks(-1), cacheOffset)));
        var completeDays = new Dictionary<DateOnly, long>();
        using (var rows = complete.ExecuteReader())
        {
            while (rows.Read())
            {
                token.ThrowIfCancellationRequested();
                completeDays.Add(DateOnly.ParseExact(rows.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                    rows.GetInt64(1));
            }
        }

        foreach (var day in rhythm.Days)
        {
            token.ThrowIfCancellationRequested();
            var start = RhythmDayStart(day.Date, reportingOffset);
            var end = start.AddDays(1);
            if (completeDays.TryGetValue(RhythmDate(start, cacheOffset), out var total) &&
                completeDays.ContainsKey(RhythmDate(end.AddTicks(-1), cacheOffset)))
                dailyTotals.Add(day.Date, reportingOffset == cacheOffset ? total : ReadRhythmTotal(totals, start, end, token) ?? 0);
        }

        // The table needs the full history, while only the curve lookback needs individual timestamps.
        var analysisStart = rhythm.Days[Math.Max(0, rhythm.Days.Length - CodexRhythmWindowAnalysis.LookbackDays)].Date;
        if (!dailyTotals.Keys.Any(date => date >= analysisStart))
            return new(dailyTotals, events);

        using var details = connection.CreateCommand();
        details.Transaction = transaction;
        details.CommandText = """
            SELECT timestamp_local, total_tokens FROM usage_events
            WHERE timestamp_local >= $start AND timestamp_local < $end
            ORDER BY timestamp_local
            """;
        details.Parameters.AddWithValue("$start", RhythmTokenTimeKey(RhythmDayStart(analysisStart, reportingOffset)));
        details.Parameters.AddWithValue("$end", RhythmTokenTimeKey(last.AddDays(1)));
        var observed = new Dictionary<DateOnly, long>();
        using (var rows = details.ExecuteReader())
        {
            while (rows.Read())
            {
                token.ThrowIfCancellationRequested();
                var stamp = ParseDateTimeOffset(rows.GetString(0));
                var date = RhythmDate(stamp, reportingOffset);
                if (!dailyTotals.ContainsKey(date)) continue;
                var total = rows.GetInt64(1);
                observed[date] = checked(observed.GetValueOrDefault(date) + total);
                events.Add(new(stamp, total));
            }
        }
        foreach (var date in dailyTotals.Keys.Where(date => date >= analysisStart).ToArray())
        {
            if (dailyTotals[date] != observed.GetValueOrDefault(date))
                dailyTotals.Remove(date);
        }

        return new(dailyTotals, events);
    }

    internal long? ReadRhythmTokenTotal(DateTimeOffset start, DateTimeOffset end, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (start >= end || !EnsureAvailable()) return null;
        using var connection = OpenConnection();
        using var command = CreateRhythmTotalCommand(connection);
        return ReadRhythmTotal(command, start, end, token);
    }

    private static SqliteCommand CreateRhythmTotalCommand(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT SUM(total_tokens) FROM usage_events
            WHERE timestamp_local >= $start AND timestamp_local < $end
            """;
        command.Parameters.Add("$start", SqliteType.Text);
        command.Parameters.Add("$end", SqliteType.Text);
        return command;
    }

    private static long? ReadRhythmTotal(
        SqliteCommand command, DateTimeOffset start, DateTimeOffset end, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        command.Parameters["$start"].Value = RhythmTokenTimeKey(start);
        command.Parameters["$end"].Value = RhythmTokenTimeKey(end);
        return command.ExecuteScalar() is long total ? total : null;
    }

    private static DateOnly RhythmDate(DateTimeOffset stamp, TimeSpan offset) =>
        DateOnly.FromDateTime(stamp.ToOffset(offset).DateTime);

    private static DateTimeOffset RhythmDayStart(DateOnly date, TimeSpan offset) =>
        new(date.ToDateTime(TimeOnly.MinValue), offset);

    private static string RhythmTokenTimeKey(DateTimeOffset stamp) =>
        FormatDateTimeOffset(stamp.ToOffset(CodexUsageReader.BeijingOffset));
}
