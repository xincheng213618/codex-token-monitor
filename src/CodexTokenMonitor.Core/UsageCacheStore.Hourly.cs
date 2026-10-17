namespace CodexTokenMonitor;

internal sealed partial class UsageCacheStore
{
    private static void EnsureHourlySchema(SqliteConnection connection)
    {
        // Rebuildable aggregates use UTC hour keys, shared by every reporting
        // timezone. Event writes invalidate only the affected hours, in the
        // same transaction as local refreshes, imports and cache deletion.
        ExecuteNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS usage_hours (
                hour_utc TEXT PRIMARY KEY,
                bucket_json TEXT NOT NULL
            );
            CREATE TRIGGER IF NOT EXISTS usage_hours_on_insert BEFORE INSERT ON usage_events
            BEGIN
                DELETE FROM usage_hours WHERE hour_utc = strftime('%Y-%m-%dT%H', substr(NEW.timestamp_local, 1, 19) || substr(NEW.timestamp_local, -6))
                    OR hour_utc = (SELECT strftime('%Y-%m-%dT%H', substr(timestamp_local, 1, 19) || substr(timestamp_local, -6))
                        FROM usage_events WHERE date = NEW.date AND event_key = NEW.event_key);
            END;
            CREATE TRIGGER IF NOT EXISTS usage_hours_on_delete AFTER DELETE ON usage_events
            BEGIN
                DELETE FROM usage_hours WHERE hour_utc = strftime('%Y-%m-%dT%H', substr(OLD.timestamp_local, 1, 19) || substr(OLD.timestamp_local, -6));
            END;
            CREATE TRIGGER IF NOT EXISTS usage_hours_on_update AFTER UPDATE ON usage_events
            BEGIN
                DELETE FROM usage_hours WHERE hour_utc IN (
                    strftime('%Y-%m-%dT%H', substr(OLD.timestamp_local, 1, 19) || substr(OLD.timestamp_local, -6)),
                    strftime('%Y-%m-%dT%H', substr(NEW.timestamp_local, 1, 19) || substr(NEW.timestamp_local, -6)));
            END;
            """);
    }

    private static TokenUsageBucket ReadHourlySlice(
        SqliteConnection connection, DateTimeOffset start, DateTimeOffset end,
        CancellationToken cancellationToken)
    {
        var total = new TokenUsageBucket { StartLocal = StartOfDay(start) };
        var firstHour = new DateTimeOffset(start.Year, start.Month, start.Day, start.Hour, 0, 0, start.Offset);
        if (firstHour < start) firstHour = firstHour.AddHours(1);
        if (firstHour > end) firstHour = end;
        // Open-ended current hours and fractional-offset day edges must be
        // clipped exactly, never rounded into a neighbouring reporting day.
        foreach (var item in ReadDetailEvents(connection, start, firstHour, cancellationToken)) total.Add(item);
        var lastHour = firstHour;
        while (lastHour.AddHours(1) <= end) lastHour = lastHour.AddHours(1);
        if (firstHour < lastHour)
        {
            using var transaction = connection.BeginTransaction();
            var cached = new Dictionary<string, TokenUsageBucket>(StringComparer.Ordinal);
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT hour_utc, bucket_json FROM usage_hours WHERE hour_utc >= $start AND hour_utc < $end";
                command.Parameters.AddWithValue("$start", HourKey(firstHour));
                command.Parameters.AddWithValue("$end", HourKey(lastHour));
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var bucket = JsonSerializer.Deserialize<TokenUsageBucket>(reader.GetString(1));
                    if (bucket is not null) cached[reader.GetString(0)] = bucket;
                }
            }
            for (var hour = firstHour; hour < lastHour; hour = hour.AddHours(1))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = HourKey(hour);
                if (!cached.TryGetValue(key, out var bucket))
                {
                    bucket = new TokenUsageBucket { StartLocal = hour };
                    foreach (var item in ReadDetailEvents(connection, hour, hour.AddHours(1), cancellationToken)) bucket.Add(item);
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = "INSERT OR REPLACE INTO usage_hours (hour_utc, bucket_json) VALUES ($hour, $json)";
                    command.Parameters.AddWithValue("$hour", key);
                    command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(bucket));
                    command.ExecuteNonQuery();
                }
                total.MergeFrom(bucket);
            }
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
        }
        foreach (var item in ReadDetailEvents(connection, lastHour, end, cancellationToken)) total.Add(item);
        return total;
    }

    private static string HourKey(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH", CultureInfo.InvariantCulture);
}
