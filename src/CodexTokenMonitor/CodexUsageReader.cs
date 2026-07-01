using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CodexTokenMonitor;

internal sealed record PriceProfile(
    string Name,
    string CurrencySymbol,
    decimal UncachedInputPerMillion,
    decimal CachedInputPerMillion,
    decimal OutputPerMillion,
    decimal Divisor);

internal static class PriceProfiles
{
    // Compatibility name: existing calculations call this property, but the
    // active GPT-5.5 lane is now configurable and defaults to Standard Short.
    public static PriceProfile Gpt55StandardLong => PriceSettingsStore.Current.ToGptProfile();

    public static PriceProfile DeepSeekV4Pro => PriceSettingsStore.Current.ToDeepSeekProfile();

    public static PriceProfile XiaomiMimoV25Pro => PriceSettingsStore.Current.ToXiaomiProfile();
}

internal static class QuotaFreshness
{
    public static readonly TimeSpan CurrentEstimateMaxAge = TimeSpan.FromHours(6);

    public static bool IsFresh(DateTimeOffset snapshotLocal, DateTimeOffset nowLocal)
    {
        return snapshotLocal <= nowLocal.AddMinutes(5) &&
               nowLocal - snapshotLocal <= CurrentEstimateMaxAge;
    }
}

internal class TokenUsageBucket
{
    public DateTimeOffset StartLocal { get; init; }
    public long Events { get; set; }
    public long InputTokens { get; set; }
    public long CachedInputTokens { get; set; }
    public long UncachedInputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long ReasoningOutputTokens { get; set; }
    public long TotalTokens { get; set; }
    public DateTimeOffset? LastTokenEventLocal { get; set; }
    public double CacheRatioPercent => InputTokens > 0 ? CachedInputTokens / (double)InputTokens * 100 : 0;

    public decimal EstimateCost(PriceProfile profile)
    {
        return (UncachedInputTokens / profile.Divisor * profile.UncachedInputPerMillion) +
               (CachedInputTokens / profile.Divisor * profile.CachedInputPerMillion) +
               (OutputTokens / profile.Divisor * profile.OutputPerMillion);
    }

    public void Add(DateTimeOffset timestamp, long input, long cached, long output, long reasoning, long total)
    {
        Events++;
        InputTokens += input;
        CachedInputTokens += cached;
        UncachedInputTokens += input - cached;
        OutputTokens += output;
        ReasoningOutputTokens += reasoning;
        TotalTokens += total;

        if (LastTokenEventLocal is null || timestamp > LastTokenEventLocal)
        {
            LastTokenEventLocal = timestamp;
        }
    }
}

internal sealed class TokenUsageSummary : TokenUsageBucket
{
    public DateTimeOffset EndLocal { get; init; }
    public List<TokenUsageBucket> DailyBuckets { get; } = new();
}

internal sealed record TokenUsageEvent(
    DateTimeOffset Timestamp,
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    long ReasoningOutputTokens,
    long TotalTokens,
    string? Key = null);

internal sealed record CodexQuotaWindowEstimate(
    string Label,
    decimal UsedPercent,
    int WindowMinutes,
    DateTimeOffset WindowStartLocal,
    DateTimeOffset WindowEndLocal,
    DateTimeOffset? ResetAtLocal,
    TokenUsageSummary Usage,
    decimal UsedGptCost,
    decimal? EstimatedGptLimit,
    long? EstimatedTokenLimit);

internal sealed record CodexQuotaEstimate(
    DateTimeOffset SnapshotLocal,
    string? LimitId,
    string? LimitName,
    CodexQuotaWindowEstimate? FiveHour,
    CodexQuotaWindowEstimate? Week);

internal sealed record CodexQuotaSnapshot(
    DateTimeOffset SnapshotLocal,
    string? LimitId,
    string? LimitName,
    decimal? FiveHourUsedPercent,
    DateTimeOffset? FiveHourResetAtLocal,
    decimal? WeekUsedPercent,
    DateTimeOffset? WeekResetAtLocal);

internal static class UsageEventMerger
{
    public static IReadOnlyList<TokenUsageEvent> Merge(IEnumerable<TokenUsageEvent> events)
    {
        return events
            .GroupBy(GetStableKey, StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(CompletenessScore)
                .ThenByDescending(item => item.Timestamp)
                .First())
            .OrderBy(item => item.Timestamp)
            .ToList();
    }

    private static string GetStableKey(TokenUsageEvent item)
    {
        return !string.IsNullOrWhiteSpace(item.Key)
            ? item.Key
            : $"{item.Timestamp:O}|{item.InputTokens}|{item.CachedInputTokens}|{item.OutputTokens}|{item.ReasoningOutputTokens}|{item.TotalTokens}";
    }

    private static long CompletenessScore(TokenUsageEvent item)
    {
        return item.InputTokens + item.CachedInputTokens + item.OutputTokens + item.ReasoningOutputTokens + item.TotalTokens;
    }
}

internal sealed class CachedUsageEvent
{
    public string? Key { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public long InputTokens { get; set; }
    public long CachedInputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long ReasoningOutputTokens { get; set; }
    public long TotalTokens { get; set; }
}

internal sealed class CachedDayRecord
{
    public string Date { get; set; } = "";
    public bool IsComplete { get; set; }
    public DateTimeOffset? ScannedThroughLocal { get; set; }
    public long Events { get; set; }
    public long InputTokens { get; set; }
    public long CachedInputTokens { get; set; }
    public long UncachedInputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long ReasoningOutputTokens { get; set; }
    public long TotalTokens { get; set; }
    public DateTimeOffset? LastTokenEventLocal { get; set; }
    public int DetailEventCount { get; set; }
    public List<CachedUsageEvent> DetailEvents { get; set; } = new();
}

internal sealed class UsageCacheStore
{
    private const string CacheFileName = "token-cache-v2.sqlite3";

    private readonly string cachePath;
    private readonly bool available;

    private UsageCacheStore(string folderName)
    {
        cachePath = GetCachePath(folderName);
        available = InitializeDatabase();
    }

    public static string GetCachePath(string folderName)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, folderName, CacheFileName);
    }

    public static bool Delete(string folderName)
    {
        var cachePath = GetCachePath(folderName);
        var deleted = false;
        foreach (var path in new[]
                 {
                     cachePath,
                     cachePath + "-wal",
                     cachePath + "-shm",
                     Path.Combine(Path.GetDirectoryName(cachePath) ?? "", "usage-cache-v1.json"),
                     Path.Combine(Path.GetDirectoryName(cachePath) ?? "", "quota-snapshot-cache-v1.json")
                 })
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    deleted = true;
                }
            }
            catch
            {
                // Cache deletion is best-effort; calculations can rebuild missing data.
            }
        }

        return deleted;
    }

    public static bool DeleteDay(string folderName, DateOnly date)
    {
        return Load(folderName).DeleteDay(date);
    }

    public static IReadOnlyList<DateTimeOffset> GetIncompleteDays(
        string folderName,
        DateTimeOffset startInclusive,
        DateTimeOffset endInclusive)
    {
        var result = new List<DateTimeOffset>();
        var cache = Load(folderName);
        var start = StartOfDay(startInclusive);
        var end = StartOfDay(endInclusive);

        for (var day = end; day >= start; day = day.AddDays(-1))
        {
            var date = DateOnly.FromDateTime(day.DateTime);
            if (!cache.TryGetRecord(date, out var record) ||
                !record.IsComplete ||
                record.Events > 0 && record.DetailEventCount == 0)
            {
                result.Add(day);
            }
        }

        return result;
    }

    private static DateTimeOffset StartOfDay(DateTimeOffset value)
    {
        return new DateTimeOffset(value.Year, value.Month, value.Day, 0, 0, 0, value.Offset);
    }

    public static UsageCacheStore Load()
    {
        return Load("CodexTokenMonitor");
    }

    public static UsageCacheStore Load(string folderName)
    {
        return new UsageCacheStore(folderName);
    }

    public bool TryGet(DateOnly date, out TokenUsageBucket bucket)
    {
        bucket = new TokenUsageBucket
        {
            StartLocal = new DateTimeOffset(date.Year, date.Month, date.Day, 0, 0, 0, CodexUsageReader.BeijingOffset)
        };

        if (!TryGetRecord(date, out var record))
        {
            return false;
        }

        bucket.Events = record.Events;
        bucket.InputTokens = record.InputTokens;
        bucket.CachedInputTokens = record.CachedInputTokens;
        bucket.UncachedInputTokens = record.UncachedInputTokens;
        bucket.OutputTokens = record.OutputTokens;
        bucket.ReasoningOutputTokens = record.ReasoningOutputTokens;
        bucket.TotalTokens = record.TotalTokens;
        bucket.LastTokenEventLocal = record.LastTokenEventLocal;
        return true;
    }

    public TokenUsageSummary ReadRange(DateTimeOffset startLocal, DateTimeOffset endLocal)
    {
        var summary = new TokenUsageSummary
        {
            StartLocal = startLocal,
            EndLocal = endLocal
        };
        var dailyBuckets = new Dictionary<DateOnly, TokenUsageBucket>();

        for (var dayStart = StartOfDay(startLocal); dayStart < endLocal; dayStart = dayStart.AddDays(1))
        {
            var dayEnd = dayStart.AddDays(1);
            var clippedStart = Max(dayStart, startLocal);
            var clippedEnd = Min(dayEnd, endLocal);
            if (clippedStart >= clippedEnd)
            {
                continue;
            }

            var date = DateOnly.FromDateTime(dayStart.DateTime);
            if (clippedStart == dayStart && clippedEnd == dayEnd)
            {
                if (TryGet(date, out var cachedBucket))
                {
                    AddBucketToSummary(summary, dailyBuckets, cachedBucket);
                }

                continue;
            }

            foreach (var usageEvent in GetDetailEvents(date)
                         .Where(item => item.Timestamp >= clippedStart && item.Timestamp < clippedEnd))
            {
                AddEventToSummary(summary, dailyBuckets, usageEvent);
            }
        }

        summary.DailyBuckets.AddRange(
            dailyBuckets.Values
                .OrderBy(bucket => bucket.StartLocal)
                .Where(bucket => bucket.Events > 0));
        return summary;
    }

    public IReadOnlyList<TokenUsageBucket> ReadDetailRows(DateTimeOffset startLocal, DateTimeOffset endLocal)
    {
        var events = new List<TokenUsageEvent>();
        for (var dayStart = StartOfDay(startLocal); dayStart < endLocal; dayStart = dayStart.AddDays(1))
        {
            var dayEnd = dayStart.AddDays(1);
            var clippedStart = Max(dayStart, startLocal);
            var clippedEnd = Min(dayEnd, endLocal);
            if (clippedStart >= clippedEnd)
            {
                continue;
            }

            var date = DateOnly.FromDateTime(dayStart.DateTime);
            events.AddRange(GetDetailEvents(date)
                .Where(item => item.Timestamp >= clippedStart && item.Timestamp < clippedEnd));
        }

        return ToDetailBuckets(events);
    }

    public bool TryGetRecord(DateOnly date, out CachedDayRecord record)
    {
        record = null!;
        if (!available)
        {
            return false;
        }

        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT date, is_complete, scanned_through_local, events, input_tokens, cached_input_tokens,
                       uncached_input_tokens, output_tokens, reasoning_output_tokens, total_tokens, last_token_event_local,
                       (SELECT COUNT(*) FROM usage_events WHERE usage_events.date = usage_days.date) AS detail_event_count
                FROM usage_days
                WHERE date = $date
                """;
            command.Parameters.AddWithValue("$date", DateKey(date));

            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return false;
            }

            record = new CachedDayRecord
            {
                Date = reader.GetString(0),
                IsComplete = reader.GetInt64(1) != 0,
                ScannedThroughLocal = ReadDateTimeOffset(reader, 2),
                Events = reader.GetInt64(3),
                InputTokens = reader.GetInt64(4),
                CachedInputTokens = reader.GetInt64(5),
                UncachedInputTokens = reader.GetInt64(6),
                OutputTokens = reader.GetInt64(7),
                ReasoningOutputTokens = reader.GetInt64(8),
                TotalTokens = reader.GetInt64(9),
                LastTokenEventLocal = ReadDateTimeOffset(reader, 10),
                DetailEventCount = reader.GetInt32(11)
            };
            return true;
        }
        catch
        {
            return false;
        }
    }

    public IReadOnlyList<TokenUsageEvent> GetDetailEvents(DateOnly date)
    {
        if (!available)
        {
            return Array.Empty<TokenUsageEvent>();
        }

        try
        {
            using var connection = OpenConnection();
            return ReadDetailEvents(connection, date);
        }
        catch
        {
            return Array.Empty<TokenUsageEvent>();
        }
    }

    public bool HasDetailEvents(DateOnly date)
    {
        if (!available)
        {
            return false;
        }

        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM usage_events WHERE date = $date LIMIT 1";
            command.Parameters.AddWithValue("$date", DateKey(date));
            return command.ExecuteScalar() is not null;
        }
        catch
        {
            return false;
        }
    }

    public bool DeleteDay(DateOnly date)
    {
        if (!available)
        {
            return false;
        }

        try
        {
            var key = DateKey(date);
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            var deleted = 0;

            using (var deleteEventsCommand = connection.CreateCommand())
            {
                deleteEventsCommand.Transaction = transaction;
                deleteEventsCommand.CommandText = "DELETE FROM usage_events WHERE date = $date";
                deleteEventsCommand.Parameters.AddWithValue("$date", key);
                deleted += deleteEventsCommand.ExecuteNonQuery();
            }

            using (var deleteDayCommand = connection.CreateCommand())
            {
                deleteDayCommand.Transaction = transaction;
                deleteDayCommand.CommandText = "DELETE FROM usage_days WHERE date = $date";
                deleteDayCommand.Parameters.AddWithValue("$date", key);
                deleted += deleteDayCommand.ExecuteNonQuery();
            }

            transaction.Commit();
            return deleted > 0;
        }
        catch
        {
            return false;
        }
    }

    public void Put(
        TokenUsageBucket bucket,
        bool isComplete = true,
        DateTimeOffset? scannedThroughLocal = null,
        IReadOnlyList<TokenUsageEvent>? detailEvents = null,
        bool replaceDetailEvents = true)
    {
        if (!available)
        {
            return;
        }

        try
        {
            var date = DateOnly.FromDateTime(bucket.StartLocal.DateTime);
            var key = DateKey(date);
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO usage_days (
                        date, is_complete, scanned_through_local, events, input_tokens, cached_input_tokens,
                        uncached_input_tokens, output_tokens, reasoning_output_tokens, total_tokens, last_token_event_local
                    )
                    VALUES (
                        $date, $is_complete, $scanned_through_local, $events, $input_tokens, $cached_input_tokens,
                        $uncached_input_tokens, $output_tokens, $reasoning_output_tokens, $total_tokens, $last_token_event_local
                    )
                    ON CONFLICT(date) DO UPDATE SET
                        is_complete = excluded.is_complete,
                        scanned_through_local = excluded.scanned_through_local,
                        events = excluded.events,
                        input_tokens = excluded.input_tokens,
                        cached_input_tokens = excluded.cached_input_tokens,
                        uncached_input_tokens = excluded.uncached_input_tokens,
                        output_tokens = excluded.output_tokens,
                        reasoning_output_tokens = excluded.reasoning_output_tokens,
                        total_tokens = excluded.total_tokens,
                        last_token_event_local = excluded.last_token_event_local
                    """;
                command.Parameters.AddWithValue("$date", key);
                command.Parameters.AddWithValue("$is_complete", isComplete ? 1 : 0);
                command.Parameters.AddWithValue("$scanned_through_local", ToDbValue(scannedThroughLocal));
                command.Parameters.AddWithValue("$events", bucket.Events);
                command.Parameters.AddWithValue("$input_tokens", bucket.InputTokens);
                command.Parameters.AddWithValue("$cached_input_tokens", bucket.CachedInputTokens);
                command.Parameters.AddWithValue("$uncached_input_tokens", bucket.UncachedInputTokens);
                command.Parameters.AddWithValue("$output_tokens", bucket.OutputTokens);
                command.Parameters.AddWithValue("$reasoning_output_tokens", bucket.ReasoningOutputTokens);
                command.Parameters.AddWithValue("$total_tokens", bucket.TotalTokens);
                command.Parameters.AddWithValue("$last_token_event_local", ToDbValue(bucket.LastTokenEventLocal));
                command.ExecuteNonQuery();
            }

            if (detailEvents is not null)
            {
                if (replaceDetailEvents)
                {
                    using var deleteCommand = connection.CreateCommand();
                    deleteCommand.Transaction = transaction;
                    deleteCommand.CommandText = "DELETE FROM usage_events WHERE date = $date";
                    deleteCommand.Parameters.AddWithValue("$date", key);
                    deleteCommand.ExecuteNonQuery();
                }

                foreach (var item in UsageEventMerger.Merge(detailEvents))
                {
                    using var insertCommand = connection.CreateCommand();
                    insertCommand.Transaction = transaction;
                    insertCommand.CommandText = """
                        INSERT OR REPLACE INTO usage_events (
                            date, event_key, timestamp_local, input_tokens, cached_input_tokens,
                            output_tokens, reasoning_output_tokens, total_tokens
                        )
                        VALUES (
                            $date, $event_key, $timestamp_local, $input_tokens, $cached_input_tokens,
                            $output_tokens, $reasoning_output_tokens, $total_tokens
                        )
                        """;
                    insertCommand.Parameters.AddWithValue("$date", key);
                    insertCommand.Parameters.AddWithValue("$event_key", BuildUsageEventKey(item));
                    insertCommand.Parameters.AddWithValue("$timestamp_local", FormatDateTimeOffset(item.Timestamp));
                    insertCommand.Parameters.AddWithValue("$input_tokens", item.InputTokens);
                    insertCommand.Parameters.AddWithValue("$cached_input_tokens", item.CachedInputTokens);
                    insertCommand.Parameters.AddWithValue("$output_tokens", item.OutputTokens);
                    insertCommand.Parameters.AddWithValue("$reasoning_output_tokens", item.ReasoningOutputTokens);
                    insertCommand.Parameters.AddWithValue("$total_tokens", item.TotalTokens);
                    insertCommand.ExecuteNonQuery();
                }
            }

            transaction.Commit();
        }
        catch
        {
            // Cache writes are best-effort; usage calculation should keep working without them.
        }
    }

    public void Save()
    {
        // SQLite writes are committed in Put().
    }

    private bool InitializeDatabase()
    {
        try
        {
            var directory = Path.GetDirectoryName(cachePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var connection = OpenConnection();
            ExecuteNonQuery(connection, "PRAGMA journal_mode=WAL;");
            ExecuteNonQuery(connection, "PRAGMA synchronous=NORMAL;");
            ExecuteNonQuery(connection, """
                CREATE TABLE IF NOT EXISTS usage_days (
                    date TEXT PRIMARY KEY,
                    is_complete INTEGER NOT NULL,
                    scanned_through_local TEXT NULL,
                    events INTEGER NOT NULL,
                    input_tokens INTEGER NOT NULL,
                    cached_input_tokens INTEGER NOT NULL,
                    uncached_input_tokens INTEGER NOT NULL,
                    output_tokens INTEGER NOT NULL,
                    reasoning_output_tokens INTEGER NOT NULL,
                    total_tokens INTEGER NOT NULL,
                    last_token_event_local TEXT NULL
                )
                """);
            ExecuteNonQuery(connection, """
                CREATE TABLE IF NOT EXISTS usage_events (
                    date TEXT NOT NULL,
                    event_key TEXT NOT NULL,
                    timestamp_local TEXT NOT NULL,
                    input_tokens INTEGER NOT NULL,
                    cached_input_tokens INTEGER NOT NULL,
                    output_tokens INTEGER NOT NULL,
                    reasoning_output_tokens INTEGER NOT NULL,
                    total_tokens INTEGER NOT NULL,
                    PRIMARY KEY (date, event_key)
                )
                """);
            ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS idx_usage_events_time ON usage_events(timestamp_local)");
            return true;
        }
        catch
        {
            return false;
        }
    }

    private SqliteConnection OpenConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = cachePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        ExecuteNonQuery(connection, "PRAGMA busy_timeout=5000;");
        return connection;
    }

    private static IReadOnlyList<TokenUsageEvent> ReadDetailEvents(SqliteConnection connection, DateOnly date)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT event_key, timestamp_local, input_tokens, cached_input_tokens,
                   output_tokens, reasoning_output_tokens, total_tokens
            FROM usage_events
            WHERE date = $date
            ORDER BY timestamp_local
            """;
        command.Parameters.AddWithValue("$date", DateKey(date));

        var result = new List<TokenUsageEvent>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new TokenUsageEvent(
                ParseDateTimeOffset(reader.GetString(1)),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetString(0)));
        }

        return UsageEventMerger.Merge(result);
    }

    private static DateTimeOffset Max(DateTimeOffset first, DateTimeOffset second)
    {
        return first >= second ? first : second;
    }

    private static DateTimeOffset Min(DateTimeOffset first, DateTimeOffset second)
    {
        return first <= second ? first : second;
    }

    private static void AddBucketToSummary(
        TokenUsageSummary summary,
        Dictionary<DateOnly, TokenUsageBucket> dailyBuckets,
        TokenUsageBucket bucket)
    {
        AddBucketValues(summary, bucket);
        var date = DateOnly.FromDateTime(bucket.StartLocal.DateTime);
        if (!dailyBuckets.TryGetValue(date, out var dailyBucket))
        {
            dailyBucket = new TokenUsageBucket { StartLocal = bucket.StartLocal };
            dailyBuckets[date] = dailyBucket;
        }

        AddBucketValues(dailyBucket, bucket);
    }

    private static void AddEventToSummary(
        TokenUsageSummary summary,
        Dictionary<DateOnly, TokenUsageBucket> dailyBuckets,
        TokenUsageEvent usageEvent)
    {
        summary.Add(
            usageEvent.Timestamp,
            usageEvent.InputTokens,
            usageEvent.CachedInputTokens,
            usageEvent.OutputTokens,
            usageEvent.ReasoningOutputTokens,
            usageEvent.TotalTokens);

        var date = DateOnly.FromDateTime(usageEvent.Timestamp.DateTime);
        if (!dailyBuckets.TryGetValue(date, out var dailyBucket))
        {
            dailyBucket = new TokenUsageBucket
            {
                StartLocal = new DateTimeOffset(date.Year, date.Month, date.Day, 0, 0, 0, CodexUsageReader.BeijingOffset)
            };
            dailyBuckets[date] = dailyBucket;
        }

        dailyBucket.Add(
            usageEvent.Timestamp,
            usageEvent.InputTokens,
            usageEvent.CachedInputTokens,
            usageEvent.OutputTokens,
            usageEvent.ReasoningOutputTokens,
            usageEvent.TotalTokens);
    }

    private static void AddBucketValues(TokenUsageBucket target, TokenUsageBucket source)
    {
        target.Events += source.Events;
        target.InputTokens += source.InputTokens;
        target.CachedInputTokens += source.CachedInputTokens;
        target.UncachedInputTokens += source.UncachedInputTokens;
        target.OutputTokens += source.OutputTokens;
        target.ReasoningOutputTokens += source.ReasoningOutputTokens;
        target.TotalTokens += source.TotalTokens;
        if (source.LastTokenEventLocal is not null &&
            (target.LastTokenEventLocal is null || source.LastTokenEventLocal > target.LastTokenEventLocal))
        {
            target.LastTokenEventLocal = source.LastTokenEventLocal;
        }
    }

    private static IReadOnlyList<TokenUsageBucket> ToDetailBuckets(IEnumerable<TokenUsageEvent> events)
    {
        return events
            .OrderBy(item => item.Timestamp)
            .Select(item =>
            {
                var bucket = new TokenUsageBucket { StartLocal = item.Timestamp };
                bucket.Add(
                    item.Timestamp,
                    item.InputTokens,
                    item.CachedInputTokens,
                    item.OutputTokens,
                    item.ReasoningOutputTokens,
                    item.TotalTokens);
                return bucket;
            })
            .ToList();
    }

    private static void ExecuteNonQuery(SqliteConnection connection, string commandText)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.ExecuteNonQuery();
    }

    private static string DateKey(DateOnly date)
    {
        return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static object ToDbValue(DateTimeOffset? value)
    {
        return value is null ? DBNull.Value : FormatDateTimeOffset(value.Value);
    }

    private static string FormatDateTimeOffset(DateTimeOffset value)
    {
        return value.ToString("O", CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset ParseDateTimeOffset(string value)
    {
        return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }

    private static DateTimeOffset? ReadDateTimeOffset(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : ParseDateTimeOffset(reader.GetString(ordinal));
    }

    private static string BuildUsageEventKey(TokenUsageEvent item)
    {
        return !string.IsNullOrWhiteSpace(item.Key)
            ? item.Key
            : $"{item.Timestamp:O}|{item.InputTokens}|{item.CachedInputTokens}|{item.OutputTokens}|{item.ReasoningOutputTokens}|{item.TotalTokens}";
    }
}

internal sealed class CachedQuotaSnapshot
{
    public DateTimeOffset SnapshotLocal { get; set; }
    public string? LimitId { get; set; }
    public string? LimitName { get; set; }
    public decimal? FiveHourUsedPercent { get; set; }
    public DateTimeOffset? FiveHourResetAtLocal { get; set; }
    public decimal? WeekUsedPercent { get; set; }
    public DateTimeOffset? WeekResetAtLocal { get; set; }
}

internal sealed class CachedQuotaDayRecord
{
    public string Date { get; set; } = "";
    public bool IsComplete { get; set; }
    public DateTimeOffset? ScannedThroughLocal { get; set; }
    public List<CachedQuotaSnapshot> Snapshots { get; set; } = new();
}

internal sealed class QuotaSnapshotCacheStore
{
    private readonly string cachePath;
    private readonly bool available;

    private QuotaSnapshotCacheStore(string folderName)
    {
        cachePath = GetCachePath(folderName);
        available = InitializeDatabase();
    }

    public static string GetCachePath(string folderName)
    {
        return UsageCacheStore.GetCachePath(folderName);
    }

    public static IReadOnlyList<DateTimeOffset> GetIncompleteDays(
        string folderName,
        DateTimeOffset startInclusive,
        DateTimeOffset endInclusive)
    {
        var result = new List<DateTimeOffset>();
        var cache = Load(folderName);
        var start = StartOfDay(startInclusive);
        var end = StartOfDay(endInclusive);

        for (var day = end; day >= start; day = day.AddDays(-1))
        {
            var date = DateOnly.FromDateTime(day.DateTime);
            if (!cache.TryGetRecord(date, out var record) ||
                !record.IsComplete ||
                HasQuotaSnapshotPrefixGap(day, record))
            {
                result.Add(day);
            }
        }

        return result;
    }

    private static bool HasQuotaSnapshotPrefixGap(DateTimeOffset dayStart, CachedQuotaDayRecord record)
    {
        if (record.Snapshots.Count == 0)
        {
            return false;
        }

        var firstSnapshot = record.Snapshots.Min(item => item.SnapshotLocal);
        return firstSnapshot > dayStart.AddMinutes(5);
    }

    private static DateTimeOffset StartOfDay(DateTimeOffset value)
    {
        return new DateTimeOffset(value.Year, value.Month, value.Day, 0, 0, 0, value.Offset);
    }

    private static DateTimeOffset Max(DateTimeOffset first, DateTimeOffset second)
    {
        return first >= second ? first : second;
    }

    private static DateTimeOffset Min(DateTimeOffset first, DateTimeOffset second)
    {
        return first <= second ? first : second;
    }

    private static void AddBucketToSummary(
        TokenUsageSummary summary,
        Dictionary<DateOnly, TokenUsageBucket> dailyBuckets,
        TokenUsageBucket bucket)
    {
        AddBucketValues(summary, bucket);
        var date = DateOnly.FromDateTime(bucket.StartLocal.DateTime);
        if (!dailyBuckets.TryGetValue(date, out var dailyBucket))
        {
            dailyBucket = new TokenUsageBucket { StartLocal = bucket.StartLocal };
            dailyBuckets[date] = dailyBucket;
        }

        AddBucketValues(dailyBucket, bucket);
    }

    private static void AddEventToSummary(
        TokenUsageSummary summary,
        Dictionary<DateOnly, TokenUsageBucket> dailyBuckets,
        TokenUsageEvent usageEvent)
    {
        summary.Add(
            usageEvent.Timestamp,
            usageEvent.InputTokens,
            usageEvent.CachedInputTokens,
            usageEvent.OutputTokens,
            usageEvent.ReasoningOutputTokens,
            usageEvent.TotalTokens);

        var date = DateOnly.FromDateTime(usageEvent.Timestamp.DateTime);
        if (!dailyBuckets.TryGetValue(date, out var dailyBucket))
        {
            dailyBucket = new TokenUsageBucket
            {
                StartLocal = new DateTimeOffset(date.Year, date.Month, date.Day, 0, 0, 0, CodexUsageReader.BeijingOffset)
            };
            dailyBuckets[date] = dailyBucket;
        }

        dailyBucket.Add(
            usageEvent.Timestamp,
            usageEvent.InputTokens,
            usageEvent.CachedInputTokens,
            usageEvent.OutputTokens,
            usageEvent.ReasoningOutputTokens,
            usageEvent.TotalTokens);
    }

    private static void AddBucketValues(TokenUsageBucket target, TokenUsageBucket source)
    {
        target.Events += source.Events;
        target.InputTokens += source.InputTokens;
        target.CachedInputTokens += source.CachedInputTokens;
        target.UncachedInputTokens += source.UncachedInputTokens;
        target.OutputTokens += source.OutputTokens;
        target.ReasoningOutputTokens += source.ReasoningOutputTokens;
        target.TotalTokens += source.TotalTokens;
        if (source.LastTokenEventLocal is not null &&
            (target.LastTokenEventLocal is null || source.LastTokenEventLocal > target.LastTokenEventLocal))
        {
            target.LastTokenEventLocal = source.LastTokenEventLocal;
        }
    }

    private static IReadOnlyList<TokenUsageBucket> ToDetailBuckets(IEnumerable<TokenUsageEvent> events)
    {
        return events
            .OrderBy(item => item.Timestamp)
            .Select(item =>
            {
                var bucket = new TokenUsageBucket { StartLocal = item.Timestamp };
                bucket.Add(
                    item.Timestamp,
                    item.InputTokens,
                    item.CachedInputTokens,
                    item.OutputTokens,
                    item.ReasoningOutputTokens,
                    item.TotalTokens);
                return bucket;
            })
            .ToList();
    }

    public static QuotaSnapshotCacheStore Load(string folderName)
    {
        return new QuotaSnapshotCacheStore(folderName);
    }

    public static bool DeleteDay(string folderName, DateOnly date)
    {
        return Load(folderName).DeleteDay(date);
    }

    public bool TryGetRecord(DateOnly date, out CachedQuotaDayRecord record)
    {
        record = null!;
        if (!available)
        {
            return false;
        }

        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT date, is_complete, scanned_through_local
                FROM quota_days
                WHERE date = $date
                """;
            command.Parameters.AddWithValue("$date", DateKey(date));
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return false;
            }

            record = new CachedQuotaDayRecord
            {
                Date = reader.GetString(0),
                IsComplete = reader.GetInt64(1) != 0,
                ScannedThroughLocal = ReadDateTimeOffset(reader, 2)
            };
            reader.Close();

            record.Snapshots = ReadSnapshots(connection, date)
                .Select(ToCachedQuotaSnapshot)
                .ToList();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public IReadOnlyList<CodexQuotaSnapshot> GetSnapshots(DateOnly date)
    {
        if (!available)
        {
            return Array.Empty<CodexQuotaSnapshot>();
        }

        try
        {
            using var connection = OpenConnection();
            return ReadSnapshots(connection, date);
        }
        catch
        {
            return Array.Empty<CodexQuotaSnapshot>();
        }
    }

    public IReadOnlyList<CodexQuotaSnapshot> GetSnapshots(DateTimeOffset startLocal, DateTimeOffset endLocal)
    {
        var result = new List<CodexQuotaSnapshot>();
        for (var dayStart = StartOfDay(startLocal); dayStart < endLocal; dayStart = dayStart.AddDays(1))
        {
            var dayEnd = dayStart.AddDays(1);
            var clippedStart = Max(dayStart, startLocal);
            var clippedEnd = Min(dayEnd, endLocal);
            if (clippedStart >= clippedEnd)
            {
                continue;
            }

            var date = DateOnly.FromDateTime(dayStart.DateTime);
            result.AddRange(GetSnapshots(date)
                .Where(item => item.SnapshotLocal >= clippedStart && item.SnapshotLocal < clippedEnd));
        }

        return result
            .GroupBy(item => $"{item.SnapshotLocal:O}|{item.LimitId ?? ""}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(item => item.SnapshotLocal).First())
            .OrderBy(item => item.SnapshotLocal)
            .ToList();
    }

    public bool DeleteDay(DateOnly date)
    {
        if (!available)
        {
            return false;
        }

        try
        {
            var key = DateKey(date);
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            var deleted = 0;

            using (var deleteSnapshotsCommand = connection.CreateCommand())
            {
                deleteSnapshotsCommand.Transaction = transaction;
                deleteSnapshotsCommand.CommandText = "DELETE FROM quota_snapshots WHERE date = $date";
                deleteSnapshotsCommand.Parameters.AddWithValue("$date", key);
                deleted += deleteSnapshotsCommand.ExecuteNonQuery();
            }

            using (var deleteDayCommand = connection.CreateCommand())
            {
                deleteDayCommand.Transaction = transaction;
                deleteDayCommand.CommandText = "DELETE FROM quota_days WHERE date = $date";
                deleteDayCommand.Parameters.AddWithValue("$date", key);
                deleted += deleteDayCommand.ExecuteNonQuery();
            }

            transaction.Commit();
            return deleted > 0;
        }
        catch
        {
            return false;
        }
    }

    public void Put(
        DateOnly date,
        IReadOnlyList<CodexQuotaSnapshot> snapshots,
        bool isComplete,
        DateTimeOffset? scannedThroughLocal)
    {
        if (!available)
        {
            return;
        }

        try
        {
            var key = DateKey(date);
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO quota_days (date, is_complete, scanned_through_local)
                    VALUES ($date, $is_complete, $scanned_through_local)
                    ON CONFLICT(date) DO UPDATE SET
                        is_complete = excluded.is_complete,
                        scanned_through_local = excluded.scanned_through_local
                    """;
                command.Parameters.AddWithValue("$date", key);
                command.Parameters.AddWithValue("$is_complete", isComplete ? 1 : 0);
                command.Parameters.AddWithValue("$scanned_through_local", ToDbValue(scannedThroughLocal));
                command.ExecuteNonQuery();
            }

            using (var deleteCommand = connection.CreateCommand())
            {
                deleteCommand.Transaction = transaction;
                deleteCommand.CommandText = "DELETE FROM quota_snapshots WHERE date = $date";
                deleteCommand.Parameters.AddWithValue("$date", key);
                deleteCommand.ExecuteNonQuery();
            }

            foreach (var snapshot in snapshots.OrderBy(item => item.SnapshotLocal))
            {
                using var insertCommand = connection.CreateCommand();
                insertCommand.Transaction = transaction;
                insertCommand.CommandText = """
                    INSERT OR REPLACE INTO quota_snapshots (
                        date, snapshot_key, snapshot_local, limit_id, limit_name,
                        five_hour_used_percent, five_hour_reset_local,
                        week_used_percent, week_reset_local
                    )
                    VALUES (
                        $date, $snapshot_key, $snapshot_local, $limit_id, $limit_name,
                        $five_hour_used_percent, $five_hour_reset_local,
                        $week_used_percent, $week_reset_local
                    )
                    """;
                insertCommand.Parameters.AddWithValue("$date", key);
                insertCommand.Parameters.AddWithValue("$snapshot_key", BuildSnapshotKey(snapshot));
                insertCommand.Parameters.AddWithValue("$snapshot_local", FormatDateTimeOffset(snapshot.SnapshotLocal));
                insertCommand.Parameters.AddWithValue("$limit_id", ToDbValue(snapshot.LimitId));
                insertCommand.Parameters.AddWithValue("$limit_name", ToDbValue(snapshot.LimitName));
                insertCommand.Parameters.AddWithValue("$five_hour_used_percent", ToDbValue(snapshot.FiveHourUsedPercent));
                insertCommand.Parameters.AddWithValue("$five_hour_reset_local", ToDbValue(snapshot.FiveHourResetAtLocal));
                insertCommand.Parameters.AddWithValue("$week_used_percent", ToDbValue(snapshot.WeekUsedPercent));
                insertCommand.Parameters.AddWithValue("$week_reset_local", ToDbValue(snapshot.WeekResetAtLocal));
                insertCommand.ExecuteNonQuery();
            }

            transaction.Commit();
        }
        catch
        {
            // Quota snapshot cache is an optimization; live parsing can still work without it.
        }
    }

    public void Save()
    {
        // SQLite writes are committed in Put().
    }

    private bool InitializeDatabase()
    {
        try
        {
            var directory = Path.GetDirectoryName(cachePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var connection = OpenConnection();
            ExecuteNonQuery(connection, "PRAGMA journal_mode=WAL;");
            ExecuteNonQuery(connection, "PRAGMA synchronous=NORMAL;");
            ExecuteNonQuery(connection, """
                CREATE TABLE IF NOT EXISTS quota_days (
                    date TEXT PRIMARY KEY,
                    is_complete INTEGER NOT NULL,
                    scanned_through_local TEXT NULL
                )
                """);
            ExecuteNonQuery(connection, """
                CREATE TABLE IF NOT EXISTS quota_snapshots (
                    date TEXT NOT NULL,
                    snapshot_key TEXT NOT NULL,
                    snapshot_local TEXT NOT NULL,
                    limit_id TEXT NULL,
                    limit_name TEXT NULL,
                    five_hour_used_percent TEXT NULL,
                    five_hour_reset_local TEXT NULL,
                    week_used_percent TEXT NULL,
                    week_reset_local TEXT NULL,
                    PRIMARY KEY (date, snapshot_key)
                )
                """);
            ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS idx_quota_snapshots_time ON quota_snapshots(snapshot_local)");
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static CodexQuotaSnapshot ToQuotaSnapshot(CachedQuotaSnapshot snapshot)
    {
        return new CodexQuotaSnapshot(
            snapshot.SnapshotLocal,
            snapshot.LimitId,
            snapshot.LimitName,
            snapshot.FiveHourUsedPercent,
            snapshot.FiveHourResetAtLocal,
            snapshot.WeekUsedPercent,
            snapshot.WeekResetAtLocal);
    }

    private static CachedQuotaSnapshot ToCachedQuotaSnapshot(CodexQuotaSnapshot snapshot)
    {
        return new CachedQuotaSnapshot
        {
            SnapshotLocal = snapshot.SnapshotLocal,
            LimitId = snapshot.LimitId,
            LimitName = snapshot.LimitName,
            FiveHourUsedPercent = snapshot.FiveHourUsedPercent,
            FiveHourResetAtLocal = snapshot.FiveHourResetAtLocal,
            WeekUsedPercent = snapshot.WeekUsedPercent,
            WeekResetAtLocal = snapshot.WeekResetAtLocal
        };
    }

    private SqliteConnection OpenConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = cachePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        ExecuteNonQuery(connection, "PRAGMA busy_timeout=5000;");
        return connection;
    }

    private static IReadOnlyList<CodexQuotaSnapshot> ReadSnapshots(SqliteConnection connection, DateOnly date)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT snapshot_local, limit_id, limit_name,
                   five_hour_used_percent, five_hour_reset_local,
                   week_used_percent, week_reset_local
            FROM quota_snapshots
            WHERE date = $date
            ORDER BY snapshot_local
            """;
        command.Parameters.AddWithValue("$date", DateKey(date));

        var result = new List<CodexQuotaSnapshot>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new CodexQuotaSnapshot(
                ParseDateTimeOffset(reader.GetString(0)),
                ReadString(reader, 1),
                ReadString(reader, 2),
                ReadDecimal(reader, 3),
                ReadDateTimeOffset(reader, 4),
                ReadDecimal(reader, 5),
                ReadDateTimeOffset(reader, 6)));
        }

        return result;
    }

    private static void ExecuteNonQuery(SqliteConnection connection, string commandText)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.ExecuteNonQuery();
    }

    private static string DateKey(DateOnly date)
    {
        return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static object ToDbValue(DateTimeOffset? value)
    {
        return value is null ? DBNull.Value : FormatDateTimeOffset(value.Value);
    }

    private static object ToDbValue(decimal? value)
    {
        return value is null ? DBNull.Value : value.Value.ToString(CultureInfo.InvariantCulture);
    }

    private static object ToDbValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;
    }

    private static string FormatDateTimeOffset(DateTimeOffset value)
    {
        return value.ToString("O", CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset ParseDateTimeOffset(string value)
    {
        return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }

    private static DateTimeOffset? ReadDateTimeOffset(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : ParseDateTimeOffset(reader.GetString(ordinal));
    }

    private static decimal? ReadDecimal(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal)
            ? null
            : decimal.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture);
    }

    private static string? ReadString(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static string BuildSnapshotKey(CodexQuotaSnapshot snapshot)
    {
        return $"{snapshot.SnapshotLocal:O}|{snapshot.LimitId ?? ""}";
    }
}

internal sealed record ScanRange(DateTimeOffset StartLocal, DateTimeOffset EndLocal, bool CacheHistoricalDays);

internal static class CodexUsageReader
{
    private const string CacheFolder = "CodexTokenMonitor";
    private const string QuotaHistoryFileName = "quota-history.jsonl";
    public static readonly TimeSpan BeijingOffset = TimeSpan.FromHours(8);

    private sealed record RateLimitWindowSnapshot(decimal UsedPercent, int WindowMinutes, DateTimeOffset? ResetAtLocal);

    private sealed record RateLimitSnapshot(
        DateTimeOffset TimestampLocal,
        string? LimitId,
        string? LimitName,
        RateLimitWindowSnapshot? Primary,
        RateLimitWindowSnapshot? Secondary);

    private sealed record QuotaHistoryKey(DateTimeOffset SnapshotLocal, string LimitId);

    public static bool ClearCache()
    {
        return UsageCacheStore.Delete(CacheFolder);
    }

    public static bool ClearCachedDay(DateOnly date)
    {
        var usageDeleted = UsageCacheStore.DeleteDay(CacheFolder, date);
        var quotaDeleted = QuotaSnapshotCacheStore.DeleteDay(CacheFolder, date);
        return usageDeleted || quotaDeleted;
    }

    public static IReadOnlyList<DateTimeOffset> GetIncompleteHistoricalDays(
        DateTimeOffset startInclusive,
        DateTimeOffset endInclusive)
    {
        return UsageCacheStore.GetIncompleteDays(CacheFolder, startInclusive, endInclusive);
    }

    public static TokenUsageSummary ReadCachedRange(DateTimeOffset startLocal, DateTimeOffset endLocal)
    {
        return UsageCacheStore.Load(CacheFolder).ReadRange(startLocal, endLocal);
    }

    public static IReadOnlyList<TokenUsageBucket> ReadCachedDetailRows(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal)
    {
        return UsageCacheStore.Load(CacheFolder).ReadDetailRows(startLocal, endLocal);
    }

    public static CodexQuotaEstimate? ReadQuotaEstimate()
    {
        var now = DateTimeOffset.UtcNow.ToOffset(BeijingOffset);
        var liveEnd = now.AddMinutes(5);
        var snapshot = ReadQuotaSnapshotsCached(StartOfDay(now), liveEnd)
            .Where(IsGeneralCodexQuotaSnapshot)
            .OrderByDescending(item => item.SnapshotLocal)
            .FirstOrDefault()
            ?? ReadCachedQuotaSnapshots(now.AddDays(-8), liveEnd)
            .Where(IsGeneralCodexQuotaSnapshot)
            .OrderByDescending(item => item.SnapshotLocal)
            .FirstOrDefault();
        if (snapshot is null || !QuotaFreshness.IsFresh(snapshot.SnapshotLocal, now))
        {
            return null;
        }

        return BuildQuotaEstimate(snapshot, now);
    }

    public static CodexQuotaEstimate? ReadCachedQuotaEstimate()
    {
        var now = DateTimeOffset.UtcNow.ToOffset(BeijingOffset);
        var snapshot = ReadCachedQuotaSnapshots(now.AddDays(-8), now.AddMinutes(5))
            .Where(IsGeneralCodexQuotaSnapshot)
            .OrderByDescending(item => item.SnapshotLocal)
            .FirstOrDefault();
        if (snapshot is null || !QuotaFreshness.IsFresh(snapshot.SnapshotLocal, now))
        {
            return null;
        }

        return BuildQuotaEstimate(snapshot, now, includeLiveToday: false);
    }

    public static IReadOnlyList<CodexQuotaSnapshot> ReadQuotaSnapshots(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal)
    {
        return ReadQuotaSnapshotsCached(startLocal, endLocal)
            .Where(IsGeneralCodexQuotaSnapshot)
            .ToList();
    }

    public static IReadOnlyList<CodexQuotaSnapshot> ReadCachedQuotaSnapshots(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal)
    {
        return QuotaSnapshotCacheStore.Load(CacheFolder).GetSnapshots(startLocal, endLocal);
    }

    public static IReadOnlyList<DateTimeOffset> GetIncompleteQuotaSnapshotDays(
        DateTimeOffset startInclusive,
        DateTimeOffset endInclusive)
    {
        return QuotaSnapshotCacheStore.GetIncompleteDays(CacheFolder, startInclusive, endInclusive);
    }

    public static void WarmQuotaSnapshotDay(DateTimeOffset dayLocal)
    {
        var dayStart = StartOfDay(dayLocal);
        var dayEnd = dayStart.AddDays(1);
        var now = DateTimeOffset.UtcNow.ToOffset(BeijingOffset);
        if (dayStart <= now && dayEnd > now)
        {
            dayEnd = now;
        }

        _ = ReadQuotaSnapshotsCached(dayStart, dayEnd);
    }

    private static IReadOnlyList<CodexQuotaSnapshot> ReadQuotaSnapshotsCached(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal)
    {
        var result = new List<CodexQuotaSnapshot>();
        var cache = QuotaSnapshotCacheStore.Load(CacheFolder);
        var cacheChanged = false;
        var now = DateTimeOffset.UtcNow.ToOffset(BeijingOffset);
        var todayStart = StartOfDay(now);

        for (var dayStart = StartOfDay(startLocal); dayStart < endLocal; dayStart = dayStart.AddDays(1))
        {
            var dayEnd = dayStart.AddDays(1);
            var clippedStart = Max(dayStart, startLocal);
            var clippedEnd = Min(dayEnd, endLocal);
            if (clippedStart >= clippedEnd)
            {
                continue;
            }

            var date = DateOnly.FromDateTime(dayStart.DateTime);
            var daySnapshots = cache.GetSnapshots(date).ToList();
            var liveToday = dayStart == todayStart;
            var effectiveClippedEnd = liveToday ? Min(clippedEnd, now) : clippedEnd;
            if (clippedStart >= effectiveClippedEnd)
            {
                continue;
            }

            var hasRecord = cache.TryGetRecord(date, out var record);
            var effectiveScannedThrough = hasRecord && liveToday
                ? GetEffectiveQuotaScannedThrough(record, now)
                : record?.ScannedThroughLocal;
            var hasPrefixGap = HasQuotaPrefixGap(clippedStart, daySnapshots);
            var hasCompleteCoverage =
                hasRecord && record is not null &&
                daySnapshots.Count > 0 &&
                !hasPrefixGap &&
                (record.IsComplete ||
                 effectiveScannedThrough is not null && effectiveScannedThrough.Value >= effectiveClippedEnd.AddTicks(-1));

            if (!hasCompleteCoverage)
            {
                var fullHistoricalDay = dayStart < todayStart;
                DateTimeOffset scanStart;
                if (hasPrefixGap)
                {
                    scanStart = clippedStart;
                }
                else if (fullHistoricalDay)
                {
                    scanStart = dayStart;
                }
                else
                {
                    scanStart = effectiveScannedThrough is null
                        ? clippedStart
                        : Max(clippedStart, effectiveScannedThrough.Value.AddTicks(1));
                }

                var scanEnd = fullHistoricalDay ? dayEnd : effectiveClippedEnd;
                if (scanStart < scanEnd)
                {
                    var scanned = ReadQuotaSnapshotsUncached(scanStart, scanEnd);
                    daySnapshots = MergeQuotaSnapshots(daySnapshots
                        .Concat(scanned)
                        .Where(item => item.SnapshotLocal >= dayStart && item.SnapshotLocal < dayEnd))
                        .ToList();
                    cache.Put(
                        date,
                        daySnapshots,
                        isComplete: fullHistoricalDay,
                        scannedThroughLocal: scanEnd.AddTicks(-1));
                    cacheChanged = true;
                }
            }

            result.AddRange(daySnapshots.Where(item => item.SnapshotLocal >= clippedStart && item.SnapshotLocal < clippedEnd));
        }

        if (cacheChanged)
        {
            cache.Save();
        }

        return MergeQuotaSnapshots(result).ToList();
    }

    private static bool HasQuotaPrefixGap(
        DateTimeOffset clippedStart,
        IReadOnlyList<CodexQuotaSnapshot> daySnapshots)
    {
        if (daySnapshots.Count == 0)
        {
            return false;
        }

        var firstSnapshot = daySnapshots.Min(item => item.SnapshotLocal);
        return firstSnapshot > clippedStart.AddMinutes(5);
    }

    private static DateTimeOffset? GetEffectiveQuotaScannedThrough(CachedQuotaDayRecord record, DateTimeOffset now)
    {
        if (record.ScannedThroughLocal is not { } scannedThrough)
        {
            return null;
        }

        if (scannedThrough <= now)
        {
            return scannedThrough;
        }

        return record.Snapshots
            .Where(item => item.SnapshotLocal <= now)
            .OrderByDescending(item => item.SnapshotLocal)
            .FirstOrDefault()
            ?.SnapshotLocal;
    }

    private static IReadOnlyList<CodexQuotaSnapshot> ReadQuotaSnapshotsUncached(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal)
    {
        var liveSnapshots = ReadRateLimitSnapshots(startLocal, endLocal);
        foreach (var item in liveSnapshots)
        {
            AppendQuotaHistoryIfNew(item);
        }

        return MergeQuotaSnapshots(liveSnapshots
                .Concat(ReadQuotaHistorySnapshots(startLocal, endLocal))
                .Where(IsQuotaHistorySnapshot)
                .Select(ToCodexQuotaSnapshot))
            .ToList();
    }

    private static IEnumerable<CodexQuotaSnapshot> MergeQuotaSnapshots(IEnumerable<CodexQuotaSnapshot> snapshots)
    {
        return snapshots
            .GroupBy(item => $"{item.SnapshotLocal:O}|{NormalizeLimitId(item.LimitId)}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(item => item.SnapshotLocal).First())
            .OrderBy(item => item.SnapshotLocal);
    }

    private static CodexQuotaEstimate BuildQuotaEstimate(RateLimitSnapshot snapshot, DateTimeOffset now)
    {
        return new CodexQuotaEstimate(
            snapshot.TimestampLocal,
            snapshot.LimitId,
            snapshot.LimitName,
            BuildQuotaWindowEstimate("5h", snapshot.Primary, now),
            BuildQuotaWindowEstimate("1周", snapshot.Secondary, now));
    }

    private static CodexQuotaEstimate BuildQuotaEstimate(
        CodexQuotaSnapshot snapshot,
        DateTimeOffset now,
        bool includeLiveToday = true)
    {
        return new CodexQuotaEstimate(
            snapshot.SnapshotLocal,
            snapshot.LimitId,
            snapshot.LimitName,
            BuildQuotaWindowEstimate(
                "5h",
                ToRateLimitWindow(snapshot.FiveHourUsedPercent, 5 * 60, snapshot.FiveHourResetAtLocal),
                now,
                includeLiveToday),
            BuildQuotaWindowEstimate(
                "1周",
                ToRateLimitWindow(snapshot.WeekUsedPercent, 7 * 24 * 60, snapshot.WeekResetAtLocal),
                now,
                includeLiveToday));
    }

    private static RateLimitWindowSnapshot? ToRateLimitWindow(
        decimal? usedPercent,
        int windowMinutes,
        DateTimeOffset? resetAtLocal)
    {
        return usedPercent is null
            ? null
            : new RateLimitWindowSnapshot(usedPercent.Value, windowMinutes, resetAtLocal);
    }

    private static CodexQuotaWindowEstimate? BuildQuotaWindowEstimate(
        string label,
        RateLimitWindowSnapshot? snapshot,
        DateTimeOffset now,
        bool includeLiveToday = true)
    {
        if (snapshot is null || snapshot.WindowMinutes <= 0)
        {
            return null;
        }

        var windowStart = snapshot.ResetAtLocal is null
            ? now.AddMinutes(-snapshot.WindowMinutes)
            : snapshot.ResetAtLocal.Value.AddMinutes(-snapshot.WindowMinutes);
        if (windowStart > now)
        {
            windowStart = now.AddMinutes(-snapshot.WindowMinutes);
        }

        var usage = ReadRangeFromDetailRows(windowStart, now, includeLiveToday);
        var usedCost = usage.EstimateCost(PriceProfiles.Gpt55StandardLong);
        var ratio = snapshot.UsedPercent / 100m;
        var estimatedCostLimit = ratio > 0 ? usedCost / ratio : (decimal?)null;
        var estimatedTokenLimit = ratio > 0 ? (long?)Math.Round(usage.TotalTokens / ratio) : null;
        return new CodexQuotaWindowEstimate(
            label,
            snapshot.UsedPercent,
            snapshot.WindowMinutes,
            windowStart,
            now,
            snapshot.ResetAtLocal,
            usage,
            usedCost,
            estimatedCostLimit,
            estimatedTokenLimit);
    }

    public static TokenUsageSummary ReadRangeFromDetailRows(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        bool includeLiveToday = true)
    {
        var summary = new TokenUsageSummary
        {
            StartLocal = startLocal,
            EndLocal = endLocal
        };
        var dailyBuckets = new Dictionary<DateOnly, TokenUsageBucket>();

        for (var segmentStart = startLocal; segmentStart < endLocal;)
        {
            var nextDay = StartOfDay(segmentStart).AddDays(1);
            var segmentEnd = nextDay < endLocal ? nextDay : endLocal;
            var rows = includeLiveToday
                ? ReadDetailRows(segmentStart, segmentEnd, includeLiveToday: true)
                : ReadCachedDetailRows(segmentStart, segmentEnd);
            foreach (var row in rows)
            {
                AddBucketToSummary(summary, dailyBuckets, row);
            }

            segmentStart = segmentEnd;
        }

        summary.DailyBuckets.AddRange(
            dailyBuckets.Values
                .OrderBy(bucket => bucket.StartLocal)
                .Where(bucket => bucket.Events > 0));

        return summary;
    }

    private static void AppendQuotaHistoryIfNew(RateLimitSnapshot snapshot)
    {
        try
        {
            var path = GetQuotaHistoryPath();
            var historyKey = new QuotaHistoryKey(snapshot.TimestampLocal, NormalizeLimitId(snapshot.LimitId));
            if (QuotaHistoryContains(path, historyKey))
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var line = JsonSerializer.Serialize(new
            {
                snapshotLocal = snapshot.TimestampLocal,
                limitId = snapshot.LimitId,
                limitName = snapshot.LimitName,
                fiveHour = ToQuotaHistoryWindow(snapshot.Primary),
                week = ToQuotaHistoryWindow(snapshot.Secondary)
            });
            File.AppendAllText(path, line + Environment.NewLine);
        }
        catch
        {
            // Quota history is best-effort; the live estimate should still render if persistence fails.
        }
    }

    private static object? ToQuotaHistoryWindow(RateLimitWindowSnapshot? window)
    {
        if (window is null)
        {
            return null;
        }

        return new
        {
            usedPercent = window.UsedPercent,
            windowMinutes = window.WindowMinutes,
            resetAtLocal = window.ResetAtLocal
        };
    }

    private static bool QuotaHistoryContains(string path, QuotaHistoryKey expected)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        foreach (var line in File.ReadLines(path).Reverse())
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var key = TryReadQuotaHistoryKey(line);
                if (key is not null &&
                    key.SnapshotLocal == expected.SnapshotLocal &&
                    string.Equals(key.LimitId, expected.LimitId, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch
            {
                // Skip a partially written final line and keep searching upward.
            }
        }

        return false;
    }

    private static QuotaHistoryKey? TryReadQuotaHistoryKey(string line)
    {
        using var doc = JsonDocument.Parse(line);
        if (!doc.RootElement.TryGetProperty("snapshotLocal", out var snapshotElement) ||
            !DateTimeOffset.TryParse(snapshotElement.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var snapshot))
        {
            return null;
        }

        var limitId = GetString(doc.RootElement, "limitId");
        return new QuotaHistoryKey(snapshot, NormalizeLimitId(limitId));
    }

    private static string NormalizeLimitId(string? limitId)
    {
        return string.IsNullOrWhiteSpace(limitId)
            ? "unknown"
            : limitId.Trim();
    }

    private static string GetQuotaHistoryPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, CacheFolder, QuotaHistoryFileName);
    }

    private static IReadOnlyList<RateLimitSnapshot> ReadQuotaHistorySnapshots(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal)
    {
        var path = GetQuotaHistoryPath();
        if (!File.Exists(path))
        {
            return Array.Empty<RateLimitSnapshot>();
        }

        var snapshots = new List<RateLimitSnapshot>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var snapshot = TryReadQuotaHistorySnapshot(line, startLocal, endLocal);
            if (snapshot is not null)
            {
                snapshots.Add(snapshot);
            }
        }

        return snapshots;
    }

    private static RateLimitSnapshot? TryReadQuotaHistorySnapshot(
        string line,
        DateTimeOffset startLocal,
        DateTimeOffset endLocal)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("snapshotLocal", out var snapshotElement) ||
                !DateTimeOffset.TryParse(snapshotElement.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var timestamp) ||
                timestamp < startLocal ||
                timestamp >= endLocal)
            {
                return null;
            }

            return new RateLimitSnapshot(
                timestamp,
                GetString(root, "limitId"),
                GetString(root, "limitName"),
                TryReadQuotaHistoryWindow(root, "fiveHour"),
                TryReadQuotaHistoryWindow(root, "week"));
        }
        catch
        {
            return null;
        }
    }

    private static RateLimitWindowSnapshot? TryReadQuotaHistoryWindow(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var window) ||
            window.ValueKind is JsonValueKind.Null)
        {
            return null;
        }

        var usedPercent = GetDecimal(window, "usedPercent");
        var windowMinutes = (int)GetInt64(window, "windowMinutes");
        if (usedPercent is null || windowMinutes <= 0)
        {
            return null;
        }

        DateTimeOffset? resetAt = null;
        if (window.TryGetProperty("resetAtLocal", out var resetElement) &&
            resetElement.ValueKind is JsonValueKind.String &&
            DateTimeOffset.TryParse(resetElement.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsedReset))
        {
            resetAt = parsedReset;
        }

        return new RateLimitWindowSnapshot(usedPercent.Value, windowMinutes, resetAt);
    }

    public static TokenUsageSummary ReadRange(DateTimeOffset startLocal, DateTimeOffset endLocal, bool includeLiveToday = true)
    {
        var summary = new TokenUsageSummary
        {
            StartLocal = startLocal,
            EndLocal = endLocal
        };
        var dailyBuckets = new Dictionary<DateOnly, TokenUsageBucket>();
        var cache = UsageCacheStore.Load(CacheFolder);
        var cacheChanged = false;
        var scanRanges = new List<ScanRange>();

        var now = DateTimeOffset.UtcNow.ToOffset(BeijingOffset);
        var todayStart = StartOfDay(now);

        for (var dayStart = StartOfDay(startLocal); dayStart < endLocal; dayStart = dayStart.AddDays(1))
        {
            var dayEnd = dayStart.AddDays(1);
            var clippedStart = Max(dayStart, startLocal);
            var clippedEnd = Min(dayEnd, endLocal);
            if (clippedStart >= clippedEnd)
            {
                continue;
            }

            var fullHistoricalDay = clippedStart == dayStart && clippedEnd == dayEnd && dayStart < todayStart;
            var liveToday = dayStart == todayStart;
            var fullCachedDay = clippedStart == dayStart && (fullHistoricalDay || liveToday);
            var date = DateOnly.FromDateTime(dayStart.DateTime);

            if (fullCachedDay && cache.TryGet(date, out var cachedBucket))
            {
                AddBucketToSummary(summary, dailyBuckets, cachedBucket);
            }

            if (fullHistoricalDay)
            {
                if (cache.TryGetRecord(date, out var record) && record.IsComplete)
                {
                    continue;
                }

                var scanStart = cache.TryGetRecord(date, out record) && record.ScannedThroughLocal is not null
                    ? record.ScannedThroughLocal.Value.AddTicks(1)
                    : dayStart;
                AddScanRange(scanRanges, Max(scanStart, clippedStart), dayEnd, cacheHistoricalDays: true);
            }
            else if (liveToday)
            {
                if (!includeLiveToday)
                {
                    continue;
                }

                var liveScanEnd = Min(clippedEnd, now);
                if (clippedStart >= liveScanEnd)
                {
                    continue;
                }

                var effectiveScannedThrough = cache.TryGetRecord(date, out var record)
                    ? GetEffectiveLiveScannedThrough(record, now)
                    : null;
                var scanStart = effectiveScannedThrough is not null
                    ? effectiveScannedThrough.Value.AddTicks(1)
                    : clippedStart;
                AddScanRange(scanRanges, Max(scanStart, clippedStart), liveScanEnd, cacheHistoricalDays: false);
            }
            else
            {
                AddScanRange(scanRanges, clippedStart, clippedEnd, cacheHistoricalDays: false);
            }
        }

        foreach (var scanRange in scanRanges)
        {
            var scanned = ReadRangeUncached(scanRange.StartLocal, scanRange.EndLocal);
            foreach (var bucket in scanned.DailyBuckets)
            {
                AddBucketToSummary(summary, dailyBuckets, bucket);
            }

            for (var dayStart = StartOfDay(scanRange.StartLocal); dayStart < scanRange.EndLocal; dayStart = dayStart.AddDays(1))
            {
                var date = DateOnly.FromDateTime(dayStart.DateTime);
                var isToday = dayStart == todayStart;
                if (!scanRange.CacheHistoricalDays && !isToday)
                {
                    continue;
                }

                var scannedBucket = scanned.DailyBuckets.FirstOrDefault(item =>
                    DateOnly.FromDateTime(item.StartLocal.DateTime) == date) ?? new TokenUsageBucket
                    {
                        StartLocal = new DateTimeOffset(date.Year, date.Month, date.Day, 0, 0, 0, BeijingOffset)
                    };

                var mergedBucket = new TokenUsageBucket { StartLocal = scannedBucket.StartLocal };
                if (cache.TryGet(date, out var existingBucket))
                {
                    AddBucketValues(mergedBucket, existingBucket);
                }

                AddBucketValues(mergedBucket, scannedBucket);
                var scannedThrough = Min(dayStart.AddDays(1), scanRange.EndLocal).AddTicks(-1);
                var isComplete = scanRange.CacheHistoricalDays && dayStart < todayStart;
                IReadOnlyList<TokenUsageEvent>? detailEvents = null;
                var replaceDetailEvents = true;
                if (cache.HasDetailEvents(date))
                {
                    var detailStart = Max(dayStart, scanRange.StartLocal);
                    var detailEnd = Min(dayStart.AddDays(1), scanRange.EndLocal);
                    var newEvents = ReadEventsUncached(detailStart, detailEnd);
                    detailEvents = newEvents
                        .Where(item => item.Timestamp >= dayStart && item.Timestamp < dayStart.AddDays(1))
                        .ToList();
                    replaceDetailEvents = false;
                }

                cache.Put(mergedBucket, isComplete, scannedThrough, detailEvents, replaceDetailEvents);
                cacheChanged = true;
            }
        }

        if (cacheChanged)
        {
            cache.Save();
        }

        summary.DailyBuckets.AddRange(
            dailyBuckets.Values
                .OrderBy(bucket => bucket.StartLocal)
                .Where(bucket => bucket.Events > 0));

        return summary;
    }

    public static IReadOnlyList<TokenUsageBucket> ReadDetailRows(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        bool includeLiveToday = true)
    {
        var dayStart = StartOfDay(startLocal);
        var date = DateOnly.FromDateTime(dayStart.DateTime);
        var now = DateTimeOffset.UtcNow.ToOffset(BeijingOffset);
        var todayStart = StartOfDay(now);
        var dayEnd = dayStart.AddDays(1);
        var effectiveEndLocal = dayStart == todayStart && includeLiveToday
            ? Min(endLocal, now)
            : endLocal;
        var cache = UsageCacheStore.Load(CacheFolder);
        var cachedEvents = cache.GetDetailEvents(date).ToList();

        if (dayStart == todayStart && !includeLiveToday)
        {
            return ToDetailBuckets(cachedEvents.Where(item => item.Timestamp >= startLocal && item.Timestamp < endLocal));
        }

        if (cache.TryGetRecord(date, out var record) && (cachedEvents.Count > 0 || record.Events == 0))
        {
            var effectiveScannedThrough = dayStart == todayStart
                ? GetEffectiveLiveScannedThrough(record, now)
                : record.ScannedThroughLocal;
            var hasCompleteCoverage = record.IsComplete ||
                                      effectiveScannedThrough is not null &&
                                      effectiveScannedThrough.Value >= effectiveEndLocal.AddTicks(-1);
            if (hasCompleteCoverage)
            {
                return ToDetailBuckets(cachedEvents.Where(item => item.Timestamp >= startLocal && item.Timestamp < endLocal));
            }

            if (includeLiveToday)
            {
                var scanStart = effectiveScannedThrough is null
                    ? startLocal
                    : Max(startLocal, effectiveScannedThrough.Value.AddTicks(1));
                if (scanStart < effectiveEndLocal)
                {
                    var newEvents = ReadEventsUncached(scanStart, effectiveEndLocal);
                    var mergedEvents = UsageEventMerger.Merge(cachedEvents
                        .Concat(newEvents)
                        .Where(item => item.Timestamp >= dayStart && item.Timestamp < dayEnd));
                    var mergedBucket = CreateBucketFromEvents(dayStart, mergedEvents);
                    var isComplete = dayStart < todayStart && effectiveEndLocal >= dayEnd;
                    cache.Put(mergedBucket, isComplete, effectiveEndLocal.AddTicks(-1), newEvents, replaceDetailEvents: false);
                    cache.Save();
                    cachedEvents = mergedEvents.ToList();
                }
            }

            return ToDetailBuckets(cachedEvents.Where(item => item.Timestamp >= startLocal && item.Timestamp < endLocal));
        }

        var fullEvents = ReadEventsUncached(startLocal, endLocal);
        var fullBucket = CreateBucketFromEvents(dayStart, fullEvents);
        var completeHistoricalDay = dayStart < todayStart && startLocal == dayStart && endLocal >= dayEnd;
        if (startLocal == dayStart)
        {
            cache.Put(fullBucket, completeHistoricalDay, endLocal.AddTicks(-1), fullEvents);
            cache.Save();
        }

        return ToDetailBuckets(fullEvents);
    }

    private static DateTimeOffset? GetEffectiveLiveScannedThrough(CachedDayRecord record, DateTimeOffset now)
    {
        if (record.ScannedThroughLocal is not { } scannedThrough)
        {
            return null;
        }

        if (scannedThrough <= now)
        {
            return scannedThrough;
        }

        return record.LastTokenEventLocal is { } lastTokenEvent && lastTokenEvent <= now
            ? lastTokenEvent
            : null;
    }

    public static IReadOnlyList<TokenUsageBucket> ReadTransientDetailRows(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal)
    {
        return ToDetailBuckets(ReadEventsUncached(startLocal, endLocal));
    }

    private static TokenUsageSummary ReadRangeUncached(DateTimeOffset startLocal, DateTimeOffset endLocal)
    {
        var summary = new TokenUsageSummary
        {
            StartLocal = startLocal,
            EndLocal = endLocal
        };
        var dailyBuckets = new Dictionary<DateOnly, TokenUsageBucket>();

        foreach (var root in GetLogRoots())
        {
            foreach (var file in EnumerateJsonlFiles(root, startLocal))
            {
                ReadFile(file, startLocal, endLocal, summary, dailyBuckets);
            }
        }

        summary.DailyBuckets.AddRange(
            dailyBuckets.Values
                .OrderBy(bucket => bucket.StartLocal)
                .Where(bucket => bucket.Events > 0));

        return summary;
    }

    private static List<TokenUsageEvent> ReadEventsUncached(DateTimeOffset startLocal, DateTimeOffset endLocal)
    {
        var events = new List<TokenUsageEvent>();

        foreach (var root in GetLogRoots())
        {
            foreach (var file in EnumerateJsonlFiles(root, startLocal))
            {
                ReadEventFile(file, startLocal, endLocal, events);
            }
        }

        return UsageEventMerger.Merge(events).ToList();
    }

    private static void ReadEventFile(
        string file,
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        List<TokenUsageEvent> events)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (!line.Contains("\"type\":\"token_count\"", StringComparison.Ordinal))
                {
                    continue;
                }

                var usageEvent = TryReadUsageEvent(line, startLocal, endLocal);
                if (usageEvent is not null)
                {
                    events.Add(usageEvent);
                }
            }
        }
        catch
        {
            return;
        }
    }

    private static IReadOnlyList<TokenUsageBucket> ToDetailBuckets(IEnumerable<TokenUsageEvent> events)
    {
        return UsageEventMerger.Merge(events)
            .Select(item =>
            {
                var bucket = new TokenUsageBucket { StartLocal = item.Timestamp };
                bucket.Add(
                    item.Timestamp,
                    item.InputTokens,
                    item.CachedInputTokens,
                    item.OutputTokens,
                    item.ReasoningOutputTokens,
                    item.TotalTokens);
                return bucket;
            })
            .ToList();
    }

    private static TokenUsageBucket CreateBucketFromEvents(
        DateTimeOffset bucketStart,
        IEnumerable<TokenUsageEvent> events)
    {
        var bucket = new TokenUsageBucket { StartLocal = bucketStart };
        foreach (var item in UsageEventMerger.Merge(events))
        {
            bucket.Add(
                item.Timestamp,
                item.InputTokens,
                item.CachedInputTokens,
                item.OutputTokens,
                item.ReasoningOutputTokens,
                item.TotalTokens);
        }

        return bucket;
    }

    private static void AddScanRange(
        List<ScanRange> scanRanges,
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        bool cacheHistoricalDays)
    {
        if (startLocal >= endLocal)
        {
            return;
        }

        if (scanRanges.Count > 0)
        {
            var previous = scanRanges[^1];
            if (previous.EndLocal == startLocal && previous.CacheHistoricalDays == cacheHistoricalDays)
            {
                scanRanges[^1] = previous with { EndLocal = endLocal };
                return;
            }
        }

        scanRanges.Add(new ScanRange(startLocal, endLocal, cacheHistoricalDays));
    }

    private static void AddBucketToSummary(
        TokenUsageSummary summary,
        Dictionary<DateOnly, TokenUsageBucket> dailyBuckets,
        TokenUsageBucket bucket)
    {
        if (bucket.Events == 0)
        {
            return;
        }

        AddBucketValues(summary, bucket);
        var dayKey = DateOnly.FromDateTime(bucket.StartLocal.DateTime);
        if (!dailyBuckets.TryGetValue(dayKey, out var dailyBucket))
        {
            dailyBucket = new TokenUsageBucket { StartLocal = bucket.StartLocal };
            dailyBuckets[dayKey] = dailyBucket;
        }

        AddBucketValues(dailyBucket, bucket);
    }

    private static void AddBucketValues(TokenUsageBucket target, TokenUsageBucket source)
    {
        target.Events += source.Events;
        target.InputTokens += source.InputTokens;
        target.CachedInputTokens += source.CachedInputTokens;
        target.UncachedInputTokens += source.UncachedInputTokens;
        target.OutputTokens += source.OutputTokens;
        target.ReasoningOutputTokens += source.ReasoningOutputTokens;
        target.TotalTokens += source.TotalTokens;

        if (target.LastTokenEventLocal is null ||
            (source.LastTokenEventLocal is not null && source.LastTokenEventLocal > target.LastTokenEventLocal))
        {
            target.LastTokenEventLocal = source.LastTokenEventLocal;
        }
    }

    private static DateTimeOffset StartOfDay(DateTimeOffset value)
    {
        var local = value.ToOffset(BeijingOffset);
        return new DateTimeOffset(local.Year, local.Month, local.Day, 0, 0, 0, BeijingOffset);
    }

    private static DateTimeOffset Max(DateTimeOffset first, DateTimeOffset second)
    {
        return first >= second ? first : second;
    }

    private static DateTimeOffset Min(DateTimeOffset first, DateTimeOffset second)
    {
        return first <= second ? first : second;
    }

    private static IEnumerable<string> GetLogRoots()
    {
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(codexHome))
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            codexHome = Path.Combine(profile, ".codex");
        }

        var sessions = Path.Combine(codexHome, "sessions");
        if (Directory.Exists(sessions))
        {
            yield return sessions;
        }

        var archived = Path.Combine(codexHome, "archived_sessions");
        if (Directory.Exists(archived))
        {
            yield return archived;
        }
    }

    private static IEnumerable<string> EnumerateJsonlFiles(string root, DateTimeOffset startLocal)
    {
        var startUtc = startLocal.UtcDateTime;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true
        };

        foreach (var file in Directory.EnumerateFiles(root, "*.jsonl", options))
        {
            FileInfo info;
            try
            {
                info = new FileInfo(file);
            }
            catch
            {
                continue;
            }

            if (info.LastWriteTimeUtc >= startUtc)
            {
                yield return file;
            }
        }
    }

    private static IReadOnlyList<RateLimitSnapshot> ReadLatestRateLimitSnapshots(DateTimeOffset startLocal, DateTimeOffset endLocal)
    {
        return ReadRateLimitSnapshots(startLocal, endLocal)
            .GroupBy(GetRateLimitKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(item => item.TimestampLocal).First())
            .OrderByDescending(item => item.TimestampLocal)
            .ToList();
    }

    private static IReadOnlyList<RateLimitSnapshot> ReadRateLimitSnapshots(DateTimeOffset startLocal, DateTimeOffset endLocal)
    {
        var snapshots = new List<RateLimitSnapshot>();
        foreach (var root in GetLogRoots())
        {
            foreach (var file in EnumerateJsonlFiles(root, startLocal))
            {
                try
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream);
                    while (reader.ReadLine() is { } line)
                    {
                        if (!line.Contains("\"type\":\"token_count\"", StringComparison.Ordinal) ||
                            !line.Contains("\"rate_limits\"", StringComparison.Ordinal))
                        {
                            continue;
                        }

                        var snapshot = TryReadRateLimitSnapshot(line, startLocal, endLocal);
                        if (snapshot is null)
                        {
                            continue;
                        }

                        snapshots.Add(snapshot);
                    }
                }
                catch
                {
                    // Ignore files that are actively being written or are not readable.
                }
            }
        }

        return snapshots;
    }

    private static RateLimitSnapshot? SelectDisplayedQuotaSnapshot(IReadOnlyList<RateLimitSnapshot> snapshots)
    {
        return snapshots
            .Where(IsDisplayedQuotaSnapshot)
            .OrderByDescending(item => item.TimestampLocal)
            .FirstOrDefault();
    }

    internal static bool IsGeneralCodexQuotaSnapshot(CodexQuotaSnapshot snapshot)
    {
        return IsGeneralCodexQuota(snapshot.LimitId, snapshot.LimitName);
    }

    private static bool IsDisplayedQuotaSnapshot(RateLimitSnapshot snapshot)
    {
        return IsGeneralCodexQuota(snapshot.LimitId, snapshot.LimitName);
    }

    private static bool IsQuotaHistorySnapshot(RateLimitSnapshot snapshot)
    {
        return string.Equals(snapshot.LimitId, "codex", StringComparison.OrdinalIgnoreCase) ||
               ContainsIgnoreCase(snapshot.LimitId, "codex") ||
               ContainsIgnoreCase(snapshot.LimitName, "codex");
    }

    private static bool IsGpt53QuotaSnapshot(RateLimitSnapshot snapshot)
    {
        return IsGpt53QuotaSnapshot(snapshot.LimitId, snapshot.LimitName);
    }

    private static bool IsGeneralCodexQuota(string? limitId, string? limitName)
    {
        return !IsGpt53QuotaSnapshot(limitId, limitName) &&
               string.Equals(limitId, "codex", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGpt53QuotaSnapshot(string? limitId, string? limitName)
    {
        return ContainsIgnoreCase(limitId, "bengalfox") ||
               ContainsIgnoreCase(limitId, "gpt-5.3") ||
               ContainsIgnoreCase(limitName, "gpt-5.3") ||
               ContainsIgnoreCase(limitName, "spark");
    }

    private static string GetRateLimitKey(RateLimitSnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.LimitId))
        {
            return snapshot.LimitId;
        }

        if (!string.IsNullOrWhiteSpace(snapshot.LimitName))
        {
            return $"name:{snapshot.LimitName}";
        }

        return "unknown";
    }

    private static bool ContainsIgnoreCase(string? value, string needle)
    {
        return value?.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static CodexQuotaSnapshot ToCodexQuotaSnapshot(RateLimitSnapshot snapshot)
    {
        return new CodexQuotaSnapshot(
            snapshot.TimestampLocal,
            snapshot.LimitId,
            snapshot.LimitName,
            snapshot.Primary?.UsedPercent,
            snapshot.Primary?.ResetAtLocal,
            snapshot.Secondary?.UsedPercent,
            snapshot.Secondary?.ResetAtLocal);
    }

    private static RateLimitSnapshot? TryReadRateLimitSnapshot(
        string line,
        DateTimeOffset startLocal,
        DateTimeOffset endLocal)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!StringEquals(root, "type", "event_msg") ||
                !root.TryGetProperty("timestamp", out var timestampElement))
            {
                return null;
            }

            var timestampText = timestampElement.GetString();
            if (string.IsNullOrWhiteSpace(timestampText))
            {
                return null;
            }

            var timestamp = DateTimeOffset.Parse(
                timestampText,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal).ToOffset(BeijingOffset);
            if (timestamp < startLocal || timestamp >= endLocal)
            {
                return null;
            }

            if (!root.TryGetProperty("payload", out var payload) ||
                !StringEquals(payload, "type", "token_count") ||
                !payload.TryGetProperty("rate_limits", out var rateLimits) ||
                rateLimits.ValueKind is JsonValueKind.Null)
            {
                return null;
            }

            var primary = TryReadRateLimitWindow(rateLimits, "primary");
            var secondary = TryReadRateLimitWindow(rateLimits, "secondary");
            if (primary is null && secondary is null)
            {
                return null;
            }

            return new RateLimitSnapshot(
                timestamp,
                GetString(rateLimits, "limit_id"),
                GetString(rateLimits, "limit_name"),
                primary,
                secondary);
        }
        catch
        {
            return null;
        }
    }

    private static RateLimitWindowSnapshot? TryReadRateLimitWindow(JsonElement rateLimits, string propertyName)
    {
        if (!rateLimits.TryGetProperty(propertyName, out var window) ||
            window.ValueKind is JsonValueKind.Null)
        {
            return null;
        }

        var usedPercent = GetDecimal(window, "used_percent");
        var windowMinutes = (int)GetInt64(window, "window_minutes");
        if (usedPercent is null || windowMinutes <= 0)
        {
            return null;
        }

        DateTimeOffset? resetAt = null;
        var resetSeconds = GetInt64(window, "resets_at");
        if (resetSeconds > 0)
        {
            resetAt = DateTimeOffset.FromUnixTimeSeconds(resetSeconds).ToOffset(BeijingOffset);
        }

        return new RateLimitWindowSnapshot(usedPercent.Value, windowMinutes, resetAt);
    }

    private static void ReadFile(
        string file,
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        TokenUsageSummary summary,
        Dictionary<DateOnly, TokenUsageBucket> dailyBuckets)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (!line.Contains("\"type\":\"token_count\"", StringComparison.Ordinal))
                {
                    continue;
                }

                ReadLine(line, startLocal, endLocal, summary, dailyBuckets);
            }
        }
        catch
        {
            return;
        }
    }

    private static void ReadLine(
        string line,
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        TokenUsageSummary summary,
        Dictionary<DateOnly, TokenUsageBucket> dailyBuckets)
    {
        var usageEvent = TryReadUsageEvent(line, startLocal, endLocal);
        if (usageEvent is null)
        {
            return;
        }

        summary.Add(
            usageEvent.Timestamp,
            usageEvent.InputTokens,
            usageEvent.CachedInputTokens,
            usageEvent.OutputTokens,
            usageEvent.ReasoningOutputTokens,
            usageEvent.TotalTokens);

        var dayKey = DateOnly.FromDateTime(usageEvent.Timestamp.DateTime);
        if (!dailyBuckets.TryGetValue(dayKey, out var bucket))
        {
            bucket = new TokenUsageBucket
            {
                StartLocal = new DateTimeOffset(dayKey.Year, dayKey.Month, dayKey.Day, 0, 0, 0, BeijingOffset)
            };
            dailyBuckets[dayKey] = bucket;
        }

        bucket.Add(
            usageEvent.Timestamp,
            usageEvent.InputTokens,
            usageEvent.CachedInputTokens,
            usageEvent.OutputTokens,
            usageEvent.ReasoningOutputTokens,
            usageEvent.TotalTokens);
    }

    private static TokenUsageEvent? TryReadUsageEvent(
        string line,
        DateTimeOffset startLocal,
        DateTimeOffset endLocal)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (!StringEquals(root, "type", "event_msg") ||
                !root.TryGetProperty("timestamp", out var timestampElement))
            {
                return null;
            }

            var timestampText = timestampElement.GetString();
            if (string.IsNullOrWhiteSpace(timestampText))
            {
                return null;
            }

            var timestamp = DateTimeOffset.Parse(
                timestampText,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal).ToOffset(BeijingOffset);

            if (timestamp < startLocal || timestamp >= endLocal)
            {
                return null;
            }

            if (!root.TryGetProperty("payload", out var payload) ||
                !StringEquals(payload, "type", "token_count") ||
                !payload.TryGetProperty("info", out var info) ||
                info.ValueKind is JsonValueKind.Null ||
                !info.TryGetProperty("last_token_usage", out var usage) ||
                usage.ValueKind is JsonValueKind.Null)
            {
                return null;
            }

            var input = GetInt64(usage, "input_tokens");
            var cached = GetInt64(usage, "cached_input_tokens");
            var output = GetInt64(usage, "output_tokens");
            var reasoning = GetInt64(usage, "reasoning_output_tokens");
            var total = GetInt64(usage, "total_tokens");
            if (total == 0)
            {
                total = input + output;
            }

            var key = payload.TryGetProperty("turn_id", out var turnIdElement)
                ? turnIdElement.GetString()
                : null;
            return new TokenUsageEvent(
                timestamp,
                input,
                cached,
                output,
                reasoning,
                total,
                string.IsNullOrWhiteSpace(key) ? null : $"codex:{key}");
        }
        catch
        {
            return null;
        }
    }

    private static bool StringEquals(JsonElement element, string propertyName, string expected)
    {
        return element.TryGetProperty(propertyName, out var value) &&
               value.ValueKind is JsonValueKind.String &&
               string.Equals(value.GetString(), expected, StringComparison.Ordinal);
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return value.ValueKind is JsonValueKind.String
            ? value.GetString()
            : value.ToString();
    }

    private static long GetInt64(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return 0;
        }

        return value.ValueKind is JsonValueKind.Number && value.TryGetInt64(out var result)
            ? result
            : 0;
    }

    private static decimal? GetDecimal(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind is JsonValueKind.Number && value.TryGetDecimal(out var result))
        {
            return result;
        }

        if (value.ValueKind is JsonValueKind.String &&
            decimal.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out result))
        {
            return result;
        }

        return null;
    }
}
