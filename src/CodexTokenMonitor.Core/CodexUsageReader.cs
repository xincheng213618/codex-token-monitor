namespace CodexTokenMonitor;

internal sealed class UsageCacheStore
{
    private const string CacheFileName = "token-cache-v3.sqlite3";
    private static readonly string[] LegacyDerivedFileNames =
    {
        "token-cache-v2.sqlite3",
        "token-cache-v2.sqlite3-wal",
        "token-cache-v2.sqlite3-shm",
        "usage-cache-v1.json",
        "quota-snapshot-cache-v1.json",
        "quota-history.jsonl"
    };
    private static readonly ConcurrentDictionary<string, Lazy<UsageCacheStore>> Stores =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly string cachePath;
    private readonly bool available;

    private UsageCacheStore(string folderName)
    {
        cachePath = GetCachePath(folderName);
        available = InitializeDatabase();
        if (available)
        {
            DeleteLegacyDerivedFiles(folderName);
        }
    }

    public static string GetCachePath(string folderName)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, folderName, CacheFileName);
    }

    public static bool Delete(string folderName)
    {
        Stores.TryRemove(folderName, out _);
        QuotaSnapshotCacheStore.Forget(folderName);
        SqliteConnection.ClearAllPools();
        var cachePath = GetCachePath(folderName);
        var deleted = false;
        var directory = Path.GetDirectoryName(cachePath) ?? "";
        foreach (var path in new[] { cachePath, cachePath + "-wal", cachePath + "-shm" }
                     .Concat(LegacyDerivedFileNames.Select(name => Path.Combine(directory, name))))
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

    private static void DeleteLegacyDerivedFiles(string folderName)
    {
        var directory = Path.GetDirectoryName(GetCachePath(folderName)) ?? "";
        foreach (var fileName in LegacyDerivedFileNames)
        {
            try
            {
                var path = Path.Combine(directory, fileName);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Derived data is rebuilt lazily. A locked legacy file can be retried next launch.
            }
        }
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
        return Stores.GetOrAdd(
            folderName,
            static name => new Lazy<UsageCacheStore>(
                () => new UsageCacheStore(name),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
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
        bucket.LongContextEvents = record.LongContextEvents;
        bucket.LongContextInputTokens = record.LongContextInputTokens;
        bucket.LongContextCachedInputTokens = record.LongContextCachedInputTokens;
        bucket.LongContextOutputTokens = record.LongContextOutputTokens;
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

        if (!available || startLocal >= endLocal)
        {
            return summary;
        }

        try
        {
            using var connection = OpenConnection();
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
                    if (TryGet(connection, date, out var cachedBucket))
                    {
                        AddBucketToSummary(summary, dailyBuckets, cachedBucket);
                    }

                    continue;
                }

                foreach (var usageEvent in ReadDetailEvents(connection, clippedStart, clippedEnd))
                {
                    AddEventToSummary(summary, dailyBuckets, usageEvent);
                }
            }
        }
        catch
        {
            return summary;
        }

        summary.DailyBuckets.AddRange(
            dailyBuckets.Values
                .OrderBy(bucket => bucket.StartLocal)
                .Where(bucket => bucket.Events > 0));
        return summary;
    }

    private static bool TryGet(SqliteConnection connection, DateOnly date, out TokenUsageBucket bucket)
    {
        bucket = new TokenUsageBucket
        {
            StartLocal = new DateTimeOffset(date.Year, date.Month, date.Day, 0, 0, 0, CodexUsageReader.BeijingOffset)
        };
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT events, input_tokens, cached_input_tokens, uncached_input_tokens,
                   output_tokens, reasoning_output_tokens, total_tokens, last_token_event_local,
                   long_context_events, long_context_input_tokens,
                   long_context_cached_input_tokens, long_context_output_tokens
            FROM usage_days
            WHERE date = $date
            """;
        command.Parameters.AddWithValue("$date", DateKey(date));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return false;
        }

        bucket.Events = reader.GetInt64(0);
        bucket.InputTokens = reader.GetInt64(1);
        bucket.CachedInputTokens = reader.GetInt64(2);
        bucket.UncachedInputTokens = reader.GetInt64(3);
        bucket.OutputTokens = reader.GetInt64(4);
        bucket.ReasoningOutputTokens = reader.GetInt64(5);
        bucket.TotalTokens = reader.GetInt64(6);
        bucket.LastTokenEventLocal = ReadDateTimeOffset(reader, 7);
        bucket.LongContextEvents = reader.GetInt64(8);
        bucket.LongContextInputTokens = reader.GetInt64(9);
        bucket.LongContextCachedInputTokens = reader.GetInt64(10);
        bucket.LongContextOutputTokens = reader.GetInt64(11);
        return true;
    }

    public IReadOnlyList<TokenUsageBucket> ReadDetailRows(DateTimeOffset startLocal, DateTimeOffset endLocal)
    {
        if (!available || startLocal >= endLocal)
        {
            return Array.Empty<TokenUsageBucket>();
        }

        try
        {
            using var connection = OpenConnection();
            return ToDetailBuckets(ReadDetailEvents(connection, startLocal, endLocal));
        }
        catch
        {
            return Array.Empty<TokenUsageBucket>();
        }
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
                       long_context_events, long_context_input_tokens, long_context_cached_input_tokens,
                       long_context_output_tokens,
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
                LongContextEvents = reader.GetInt64(11),
                LongContextInputTokens = reader.GetInt64(12),
                LongContextCachedInputTokens = reader.GetInt64(13),
                LongContextOutputTokens = reader.GetInt64(14),
                DetailEventCount = reader.GetInt32(15)
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
                        uncached_input_tokens, output_tokens, reasoning_output_tokens, total_tokens, last_token_event_local,
                        long_context_events, long_context_input_tokens, long_context_cached_input_tokens,
                        long_context_output_tokens
                    )
                    VALUES (
                        $date, $is_complete, $scanned_through_local, $events, $input_tokens, $cached_input_tokens,
                        $uncached_input_tokens, $output_tokens, $reasoning_output_tokens, $total_tokens, $last_token_event_local,
                        $long_context_events, $long_context_input_tokens, $long_context_cached_input_tokens,
                        $long_context_output_tokens
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
                        last_token_event_local = excluded.last_token_event_local,
                        long_context_events = excluded.long_context_events,
                        long_context_input_tokens = excluded.long_context_input_tokens,
                        long_context_cached_input_tokens = excluded.long_context_cached_input_tokens,
                        long_context_output_tokens = excluded.long_context_output_tokens
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
                command.Parameters.AddWithValue("$long_context_events", bucket.LongContextEvents);
                command.Parameters.AddWithValue("$long_context_input_tokens", bucket.LongContextInputTokens);
                command.Parameters.AddWithValue("$long_context_cached_input_tokens", bucket.LongContextCachedInputTokens);
                command.Parameters.AddWithValue("$long_context_output_tokens", bucket.LongContextOutputTokens);
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
                    last_token_event_local TEXT NULL,
                    long_context_events INTEGER NOT NULL DEFAULT 0,
                    long_context_input_tokens INTEGER NOT NULL DEFAULT 0,
                    long_context_cached_input_tokens INTEGER NOT NULL DEFAULT 0,
                    long_context_output_tokens INTEGER NOT NULL DEFAULT 0
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
            EnsureLongContextColumns(connection);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void EnsureLongContextColumns(SqliteConnection connection)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info(usage_days)";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                columns.Add(reader.GetString(1));
            }
        }

        var addedColumn = false;
        foreach (var column in new[]
                 {
                     "long_context_events",
                     "long_context_input_tokens",
                     "long_context_cached_input_tokens",
                     "long_context_output_tokens"
                 })
        {
            if (columns.Contains(column))
            {
                continue;
            }

            ExecuteNonQuery(connection, $"ALTER TABLE usage_days ADD COLUMN {column} INTEGER NOT NULL DEFAULT 0");
            addedColumn = true;
        }

        if (!addedColumn)
        {
            return;
        }

        ExecuteNonQuery(connection, $"""
            UPDATE usage_days
            SET long_context_events = COALESCE((
                    SELECT COUNT(*) FROM usage_events e
                    WHERE e.date = usage_days.date AND e.input_tokens > {UsageTelemetryRules.OpenAiLongContextThresholdTokens}
                ), 0),
                long_context_input_tokens = COALESCE((
                    SELECT SUM(e.input_tokens) FROM usage_events e
                    WHERE e.date = usage_days.date AND e.input_tokens > {UsageTelemetryRules.OpenAiLongContextThresholdTokens}
                ), 0),
                long_context_cached_input_tokens = COALESCE((
                    SELECT SUM(e.cached_input_tokens) FROM usage_events e
                    WHERE e.date = usage_days.date AND e.input_tokens > {UsageTelemetryRules.OpenAiLongContextThresholdTokens}
                ), 0),
                long_context_output_tokens = COALESCE((
                    SELECT SUM(e.output_tokens) FROM usage_events e
                    WHERE e.date = usage_days.date AND e.input_tokens > {UsageTelemetryRules.OpenAiLongContextThresholdTokens}
                ), 0)
            """);
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

    private static IReadOnlyList<TokenUsageEvent> ReadDetailEvents(
        SqliteConnection connection,
        DateTimeOffset startLocal,
        DateTimeOffset endLocal)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT timestamp_local, input_tokens, cached_input_tokens, output_tokens,
                   reasoning_output_tokens, total_tokens, event_key
            FROM usage_events
            WHERE timestamp_local >= $start AND timestamp_local < $end
            ORDER BY timestamp_local
            """;
        command.Parameters.AddWithValue("$start", FormatDateTimeOffset(startLocal));
        command.Parameters.AddWithValue("$end", FormatDateTimeOffset(endLocal));

        var result = new List<TokenUsageEvent>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new TokenUsageEvent(
                ParseDateTimeOffset(reader.GetString(0)),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetString(6)));
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
        target.MergeFrom(source);
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
    private static readonly ConcurrentDictionary<string, Lazy<QuotaSnapshotCacheStore>> Stores =
        new(StringComparer.OrdinalIgnoreCase);
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
                !record.IsComplete)
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
        target.MergeFrom(source);
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
        return Stores.GetOrAdd(
            folderName,
            static name => new Lazy<QuotaSnapshotCacheStore>(
                () => new QuotaSnapshotCacheStore(name),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    internal static void Forget(string folderName)
    {
        Stores.TryRemove(folderName, out _);
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

    public IReadOnlyList<CodexQuotaSnapshot> GetTimelineSnapshots(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal)
    {
        if (!available || startLocal >= endLocal)
        {
            return Array.Empty<CodexQuotaSnapshot>();
        }

        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT anchor_local, limit_id, limit_name,
                       five_hour_used_percent, five_hour_reset_local,
                       week_used_percent, week_reset_local
                FROM quota_7d_timeline
                WHERE anchor_local >= $start_local AND anchor_local < $end_local
                ORDER BY anchor_local
                """;
            command.Parameters.AddWithValue("$start_local", FormatDateTimeOffset(startLocal));
            command.Parameters.AddWithValue("$end_local", FormatDateTimeOffset(endLocal));

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
        catch
        {
            return Array.Empty<CodexQuotaSnapshot>();
        }
    }

    public void PutTimelineSnapshots(
        IReadOnlyList<CodexQuotaSnapshot> snapshots,
        IReadOnlyDictionary<DateTimeOffset, (DateTimeOffset? Before, DateTimeOffset? After)> sources)
    {
        if (!available || snapshots.Count == 0)
        {
            return;
        }

        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            foreach (var snapshot in snapshots.OrderBy(item => item.SnapshotLocal))
            {
                sources.TryGetValue(snapshot.SnapshotLocal, out var source);
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO quota_7d_timeline (
                        date, anchor_local, limit_id, limit_name,
                        five_hour_used_percent, five_hour_reset_local,
                        week_used_percent, week_reset_local,
                        is_interpolated, before_snapshot_local, after_snapshot_local, updated_local
                    )
                    VALUES (
                        $date, $anchor_local, $limit_id, $limit_name,
                        $five_hour_used_percent, $five_hour_reset_local,
                        $week_used_percent, $week_reset_local,
                        $is_interpolated, $before_snapshot_local, $after_snapshot_local, $updated_local
                    )
                    ON CONFLICT(anchor_local) DO UPDATE SET
                        date = excluded.date,
                        limit_id = excluded.limit_id,
                        limit_name = excluded.limit_name,
                        five_hour_used_percent = excluded.five_hour_used_percent,
                        five_hour_reset_local = excluded.five_hour_reset_local,
                        week_used_percent = excluded.week_used_percent,
                        week_reset_local = excluded.week_reset_local,
                        is_interpolated = excluded.is_interpolated,
                        before_snapshot_local = excluded.before_snapshot_local,
                        after_snapshot_local = excluded.after_snapshot_local,
                        updated_local = excluded.updated_local
                    """;
                command.Parameters.AddWithValue("$date", DateKey(DateOnly.FromDateTime(snapshot.SnapshotLocal.DateTime)));
                command.Parameters.AddWithValue("$anchor_local", FormatDateTimeOffset(snapshot.SnapshotLocal));
                command.Parameters.AddWithValue("$limit_id", ToDbValue(snapshot.LimitId));
                command.Parameters.AddWithValue("$limit_name", ToDbValue(snapshot.LimitName));
                command.Parameters.AddWithValue("$five_hour_used_percent", ToDbValue(snapshot.FiveHourUsedPercent));
                command.Parameters.AddWithValue("$five_hour_reset_local", ToDbValue(snapshot.FiveHourResetAtLocal));
                command.Parameters.AddWithValue("$week_used_percent", ToDbValue(snapshot.WeekUsedPercent));
                command.Parameters.AddWithValue("$week_reset_local", ToDbValue(snapshot.WeekResetAtLocal));
                command.Parameters.AddWithValue("$is_interpolated", source.Before != snapshot.SnapshotLocal || source.After != snapshot.SnapshotLocal ? 1 : 0);
                command.Parameters.AddWithValue("$before_snapshot_local", ToDbValue(source.Before));
                command.Parameters.AddWithValue("$after_snapshot_local", ToDbValue(source.After));
                command.Parameters.AddWithValue("$updated_local", FormatDateTimeOffset(DateTimeOffset.UtcNow.ToOffset(CodexUsageReader.BeijingOffset)));
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }
        catch
        {
            // Timeline materialization is optional; raw snapshots remain available as a fallback.
        }
    }

    public IReadOnlyList<DateTimeOffset> GetIncompleteTimelineDays(
        DateTimeOffset startInclusive,
        DateTimeOffset endInclusive)
    {
        if (!available)
        {
            return Array.Empty<DateTimeOffset>();
        }

        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT d.date
                FROM usage_days d
                WHERE d.date >= $start_date AND d.date <= $end_date
                  AND d.events > 0
                  AND EXISTS (
                      SELECT 1
                      FROM usage_events e
                      WHERE e.date = d.date
                        AND NOT EXISTS (
                            SELECT 1
                            FROM quota_7d_timeline q
                            WHERE q.anchor_local = e.timestamp_local
                        )
                  )
                ORDER BY d.date DESC
                """;
            command.Parameters.AddWithValue("$start_date", DateKey(DateOnly.FromDateTime(startInclusive.DateTime)));
            command.Parameters.AddWithValue("$end_date", DateKey(DateOnly.FromDateTime(endInclusive.DateTime)));

            var result = new List<DateTimeOffset>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var date = DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture);
                result.Add(new DateTimeOffset(date.Year, date.Month, date.Day, 0, 0, 0, CodexUsageReader.BeijingOffset));
            }

            return result;
        }
        catch
        {
            return Array.Empty<DateTimeOffset>();
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

            deleted += DeleteTimelineRange(connection, transaction, StartOfDay(new DateTimeOffset(date.Year, date.Month, date.Day, 0, 0, 0, CodexUsageReader.BeijingOffset)).AddDays(-1), StartOfDay(new DateTimeOffset(date.Year, date.Month, date.Day, 0, 0, 0, CodexUsageReader.BeijingOffset)).AddDays(2));

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

            var dayStart = new DateTimeOffset(date.Year, date.Month, date.Day, 0, 0, 0, CodexUsageReader.BeijingOffset);
            DeleteTimelineRange(connection, transaction, dayStart.AddDays(-1), dayStart.AddDays(2));

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
            ExecuteNonQuery(connection, """
                CREATE TABLE IF NOT EXISTS quota_7d_timeline (
                    date TEXT NOT NULL,
                    anchor_local TEXT PRIMARY KEY,
                    limit_id TEXT NULL,
                    limit_name TEXT NULL,
                    five_hour_used_percent TEXT NULL,
                    five_hour_reset_local TEXT NULL,
                    week_used_percent TEXT NULL,
                    week_reset_local TEXT NULL,
                    is_interpolated INTEGER NOT NULL DEFAULT 0,
                    before_snapshot_local TEXT NULL,
                    after_snapshot_local TEXT NULL,
                    updated_local TEXT NOT NULL
                )
                """);
            ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS idx_quota_7d_timeline_date ON quota_7d_timeline(date)");
            ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS idx_quota_7d_timeline_anchor ON quota_7d_timeline(anchor_local)");
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

    private static int DeleteTimelineRange(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset startLocal,
        DateTimeOffset endLocal)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM quota_7d_timeline
            WHERE anchor_local >= $start_local AND anchor_local < $end_local
            """;
        command.Parameters.AddWithValue("$start_local", FormatDateTimeOffset(startLocal));
        command.Parameters.AddWithValue("$end_local", FormatDateTimeOffset(endLocal));
        return command.ExecuteNonQuery();
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

/// <summary>
/// Child rollout files begin with a timestamp-rewritten replay of the parent task.
/// Only token_count records after the child task boundary belong to the child.
/// </summary>
internal sealed class SubagentReplayFilter
{
    private const string CollaborationBootstrap =
        "You are an agent in a team of agents collaborating to complete a task.";

    private bool metadataChecked;
    private bool isSubagent;
    private bool waitingForLiveTaskStart;
    private bool liveTrafficStarted = true;
    private DateTimeOffset? lastReplayTimestamp;

    public bool ShouldReadTokenCount(string line)
    {
        var isTokenCount = line.Contains("\"type\":\"token_count\"", StringComparison.Ordinal);

        if (!metadataChecked && line.Contains("\"type\":\"session_meta\"", StringComparison.Ordinal))
        {
            metadataChecked = true;
            isSubagent = line.Contains("\"thread_source\":\"subagent\"", StringComparison.Ordinal) ||
                         (line.Contains("\"forked_from_id\"", StringComparison.Ordinal) &&
                          line.Contains("\"parent_thread_id\"", StringComparison.Ordinal));
            liveTrafficStarted = !isSubagent;
            lastReplayTimestamp = TryReadRecordTimestamp(line);
            return false;
        }

        if (!isSubagent || liveTrafficStarted)
        {
            return isTokenCount;
        }

        if (line.Contains(CollaborationBootstrap, StringComparison.Ordinal))
        {
            waitingForLiveTaskStart = true;
            return false;
        }

        if (waitingForLiveTaskStart &&
            line.Contains("\"type\":\"task_started\"", StringComparison.Ordinal))
        {
            liveTrafficStarted = true;
            return false;
        }

        if (line.Contains("\"type\":\"inter_agent_communication_metadata\"", StringComparison.Ordinal))
        {
            liveTrafficStarted = true;
            return false;
        }

        if (!isTokenCount)
        {
            return false;
        }

        var timestamp = TryReadRecordTimestamp(line);
        var previousTimestamp = lastReplayTimestamp;
        if (timestamp is not null)
        {
            lastReplayTimestamp = timestamp;
        }

        if (timestamp is not null && previousTimestamp is not null &&
            timestamp.Value - previousTimestamp.Value >= TimeSpan.FromSeconds(1))
        {
            liveTrafficStarted = true;
            return true;
        }

        return false;
    }

    private static DateTimeOffset? TryReadRecordTimestamp(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.TryGetProperty("timestamp", out var timestampElement) &&
                timestampElement.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(
                    timestampElement.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out var timestamp))
            {
                return timestamp;
            }
        }
        catch
        {
            // A malformed record is ignored by the normal JSONL parser as well.
        }

        return null;
    }
}

internal static class CodexUsageReader
{
    private const string CacheFolder = "CodexTokenMonitor";
    private const string QuotaHistoryFileName = "quota-history-v2.jsonl";
    private const int FiveHourWindowMinutes = 5 * 60;
    private const int WeeklyWindowMinutes = 7 * 24 * 60;
    private const long SparkContextWindowUpperBound = 128_000;
    private const string SparkLimitId = "codex_bengalfox";
    private const string SparkLimitName = "GPT-5.3-Codex-Spark";
    public static readonly TimeSpan BeijingOffset = TimeSpan.FromHours(8);
    private static readonly LiveFileTailReader UsageTailReader = new();
    private static readonly LiveFileTailReader QuotaTailReader = new();
    private static readonly ConcurrentDictionary<string, SubagentReplayFilter> UsageReplayFilters =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, SubagentReplayFilter> QuotaReplayFilters =
        new(StringComparer.OrdinalIgnoreCase);

    private sealed record RateLimitWindowSnapshot(decimal UsedPercent, int WindowMinutes, DateTimeOffset? ResetAtLocal);

    private sealed record RateLimitSnapshot(
        DateTimeOffset TimestampLocal,
        string? LimitId,
        string? LimitName,
        RateLimitWindowSnapshot? FiveHour,
        RateLimitWindowSnapshot? Week,
        long ModelContextWindow);

    private sealed record QuotaHistoryKey(DateTimeOffset SnapshotLocal, string LimitId);

    private sealed record MaterializedQuotaPoint(
        CodexQuotaSnapshot Snapshot,
        DateTimeOffset? BeforeSnapshotLocal,
        DateTimeOffset? AfterSnapshotLocal);

    public static bool ClearCache()
    {
        ResetLiveFileCursors();
        return UsageCacheStore.Delete(CacheFolder);
    }

    public static bool ClearCachedDay(DateOnly date)
    {
        ResetLiveFileCursors();
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
        var directSnapshot = CodexAppServerQuotaReader.ReadCurrent();
        if (directSnapshot is not null)
        {
            directSnapshot = NormalizeQuotaSnapshotWindows(directSnapshot);
            if (IsGeneralCodexQuotaSnapshot(directSnapshot))
            {
                AppendQuotaHistoryIfNew(ToRateLimitSnapshot(directSnapshot));
                return BuildQuotaEstimate(directSnapshot, now);
            }
        }

        // Older CLI builds do not expose account/rateLimits/read. Keep the
        // session-log path as a compatibility fallback in that case.
        var liveEnd = now.AddMinutes(5);
        var recentStart = now.AddMinutes(-30);
        var snapshot = ReadQuotaSnapshotsUncached(recentStart, liveEnd)
            .Where(IsGeneralCodexQuotaSnapshot)
            .OrderByDescending(item => item.SnapshotLocal)
            .FirstOrDefault()
            ?? ReadQuotaSnapshotsCached(recentStart, liveEnd)
            .Where(IsGeneralCodexQuotaSnapshot)
            .OrderByDescending(item => item.SnapshotLocal)
            .FirstOrDefault()
            ?? ReadQuotaSnapshotsCached(StartOfDay(now), liveEnd)
            .Where(IsGeneralCodexQuotaSnapshot)
            .OrderByDescending(item => item.SnapshotLocal)
            .FirstOrDefault()
            ?? ReadCachedAndHistoricalQuotaSnapshots(now.AddDays(-8), liveEnd)
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
        var snapshot = ReadCachedAndHistoricalQuotaSnapshots(now.AddDays(-8), now.AddMinutes(5))
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
        return ReadCachedAndHistoricalQuotaSnapshots(startLocal, endLocal)
            .Where(IsGeneralCodexQuotaSnapshot)
            .ToList();
    }

    public static IReadOnlyList<CodexQuotaSnapshot> ReadCachedQuotaSnapshots(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal)
    {
        return QuotaSnapshotCacheStore.Load(CacheFolder)
            .GetSnapshots(startLocal, endLocal)
            .Select(NormalizeQuotaSnapshotWindows)
            .ToList();
    }

    public static IReadOnlyList<CodexQuotaSnapshot> ReadMaterializedQuotaTimeline(
        IEnumerable<DateTimeOffset> anchors,
        IEnumerable<CodexQuotaSnapshot>? supplementalSnapshots = null,
        bool refreshExisting = false)
    {
        var normalizedAnchors = anchors
            .Select(anchor => anchor.ToOffset(BeijingOffset))
            .Distinct()
            .OrderBy(anchor => anchor)
            .ToList();
        if (normalizedAnchors.Count == 0)
        {
            return Array.Empty<CodexQuotaSnapshot>();
        }

        var cache = QuotaSnapshotCacheStore.Load(CacheFolder);
        var cached = cache.GetTimelineSnapshots(normalizedAnchors[0], normalizedAnchors[^1].AddTicks(1))
            .Select(NormalizeQuotaSnapshotWindows)
            .ToList();
        var cachedByAnchor = cached.ToDictionary(item => item.SnapshotLocal, item => item);
        var missingAnchors = refreshExisting
            ? normalizedAnchors
            : normalizedAnchors.Where(anchor => !cachedByAnchor.ContainsKey(anchor)).ToList();

        if (missingAnchors.Count > 0)
        {
            var sourceStart = StartOfDay(missingAnchors[0]).AddDays(-1);
            var sourceEnd = StartOfDay(missingAnchors[^1]).AddDays(2);
            var sourceSnapshots = PrepareQuotaTimelineSources(
                ReadCachedAndHistoricalQuotaSnapshots(sourceStart, sourceEnd)
                    .Concat(supplementalSnapshots ?? Array.Empty<CodexQuotaSnapshot>()));
            var materialized = missingAnchors
                .Select(anchor => MaterializeQuotaPoint(anchor, sourceSnapshots))
                .ToList();
            var sourceMap = materialized.ToDictionary(
                item => item.Snapshot.SnapshotLocal,
                item => (item.BeforeSnapshotLocal, item.AfterSnapshotLocal));
            cache.PutTimelineSnapshots(materialized.Select(item => item.Snapshot).ToList(), sourceMap);

            foreach (var point in materialized)
            {
                cachedByAnchor[point.Snapshot.SnapshotLocal] = point.Snapshot;
            }
        }

        return normalizedAnchors
            .Where(cachedByAnchor.ContainsKey)
            .Select(anchor => cachedByAnchor[anchor])
            .ToList();
    }

    public static IReadOnlyList<DateTimeOffset> GetIncompleteQuotaTimelineDays(
        DateTimeOffset startInclusive,
        DateTimeOffset endInclusive)
    {
        return QuotaSnapshotCacheStore.Load(CacheFolder)
            .GetIncompleteTimelineDays(startInclusive, endInclusive);
    }

    public static void WarmQuotaTimelineDay(DateTimeOffset dayLocal)
    {
        var dayStart = StartOfDay(dayLocal);
        var dayEnd = dayStart.AddDays(1);
        var rows = ReadCachedDetailRows(dayStart, dayEnd);
        if (rows.Count == 0)
        {
            return;
        }

        _ = ReadMaterializedQuotaTimeline(rows.Select(row => row.StartLocal));
    }

    public static IReadOnlyList<CodexQuotaSnapshot> ReadCachedAndHistoricalQuotaSnapshots(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal)
    {
        var cached = ReadCachedQuotaSnapshots(startLocal, endLocal);
        var history = ReadQuotaHistoryQuotaSnapshots(startLocal, endLocal);
        var historySparkTimes = history
            .Where(IsGpt53QuotaSnapshot)
            .Select(item => item.SnapshotLocal)
            .ToHashSet();

        return MergeQuotaSnapshots(cached
                .Where(item => !IsStaleGeneralSnapshotForSpark(item, historySparkTimes))
                .Concat(history))
            .ToList();
    }

    private static IReadOnlyList<CodexQuotaSnapshot> PrepareQuotaTimelineSources(
        IEnumerable<CodexQuotaSnapshot> snapshots)
    {
        var filtered = MergeQuotaSnapshots(snapshots)
            .Where(IsGeneralCodexQuotaSnapshot)
            .OrderBy(item => item.SnapshotLocal)
            .ToList();
        if (filtered.Count == 0)
        {
            return filtered;
        }

        var useful = filtered.Where(HasTimelineQuotaUsage).ToList();
        if (useful.Count > 0)
        {
            filtered = filtered
                .Where(snapshot =>
                    !IsZeroTimelineQuotaSnapshot(snapshot) ||
                    !useful.Any(other =>
                        Math.Abs((other.SnapshotLocal - snapshot.SnapshotLocal).TotalMinutes) <= 10 &&
                        TimelineQuotaWindowsOverlap(snapshot, other)))
                .ToList();
        }

        return CodexQuotaCycleReader.MarkTransientResetOutliers(filtered)
            .Where(item => !item.IsAnomaly)
            .OrderBy(item => item.SnapshotLocal)
            .ToList();
    }

    private static MaterializedQuotaPoint MaterializeQuotaPoint(
        DateTimeOffset anchor,
        IReadOnlyList<CodexQuotaSnapshot> sources)
    {
        var before = sources.LastOrDefault(item => item.SnapshotLocal <= anchor);
        var after = sources.FirstOrDefault(item => item.SnapshotLocal >= anchor);
        var nearest = sources
            .OrderBy(item => Math.Abs((item.SnapshotLocal - anchor).TotalSeconds))
            .FirstOrDefault();
        if (nearest is not null && Math.Abs((nearest.SnapshotLocal - anchor).TotalMinutes) <= 2)
        {
            return new MaterializedQuotaPoint(
                nearest with { SnapshotLocal = anchor, IsAnomaly = false },
                nearest.SnapshotLocal,
                nearest.SnapshotLocal);
        }

        var fiveHour = InterpolateTimelineWindow(
            anchor,
            sources,
            item => item.FiveHourUsedPercent,
            item => item.FiveHourResetAtLocal);
        var week = InterpolateTimelineWindow(
            anchor,
            sources,
            item => item.WeekUsedPercent,
            item => item.WeekResetAtLocal);
        var identity = nearest ?? before ?? after;
        return new MaterializedQuotaPoint(
            new CodexQuotaSnapshot(
                anchor,
                identity?.LimitId,
                identity?.LimitName,
                fiveHour.UsedPercent,
                fiveHour.ResetAtLocal,
                week.UsedPercent,
                week.ResetAtLocal),
            before?.SnapshotLocal,
            after?.SnapshotLocal);
    }

    private static (decimal? UsedPercent, DateTimeOffset? ResetAtLocal) InterpolateTimelineWindow(
        DateTimeOffset anchor,
        IReadOnlyList<CodexQuotaSnapshot> sources,
        Func<CodexQuotaSnapshot, decimal?> usedSelector,
        Func<CodexQuotaSnapshot, DateTimeOffset?> resetSelector)
    {
        var before = sources.LastOrDefault(item =>
            item.SnapshotLocal <= anchor && usedSelector(item) is not null);
        var after = sources.FirstOrDefault(item =>
            item.SnapshotLocal >= anchor && usedSelector(item) is not null);
        if (before is not null && after is not null)
        {
            var beforeUsed = usedSelector(before)!.Value;
            var afterUsed = usedSelector(after)!.Value;
            var beforeReset = resetSelector(before);
            var afterReset = resetSelector(after);
            if (before.SnapshotLocal == after.SnapshotLocal)
            {
                return (ClampTimelinePercent(beforeUsed), beforeReset ?? afterReset);
            }

            if (SameTimelineQuotaReset(beforeReset, afterReset) && afterUsed + 1m >= beforeUsed)
            {
                var ratio = (decimal)((anchor - before.SnapshotLocal).TotalSeconds /
                                      (after.SnapshotLocal - before.SnapshotLocal).TotalSeconds);
                return (
                    ClampTimelinePercent(beforeUsed + ((afterUsed - beforeUsed) * ratio)),
                    afterReset ?? beforeReset);
            }
        }

        var nearest = sources
            .Where(item => usedSelector(item) is not null)
            .OrderBy(item => Math.Abs((item.SnapshotLocal - anchor).TotalSeconds))
            .FirstOrDefault();
        return nearest is not null && Math.Abs((nearest.SnapshotLocal - anchor).TotalMinutes) <= 10
            ? (ClampTimelinePercent(usedSelector(nearest)!.Value), resetSelector(nearest))
            : (null, null);
    }

    private static bool IsZeroTimelineQuotaSnapshot(CodexQuotaSnapshot snapshot)
    {
        return snapshot.FiveHourUsedPercent == 0m && snapshot.WeekUsedPercent == 0m;
    }

    private static bool HasTimelineQuotaUsage(CodexQuotaSnapshot snapshot)
    {
        return (snapshot.FiveHourUsedPercent ?? 0m) > 0m ||
               (snapshot.WeekUsedPercent ?? 0m) > 0m;
    }

    private static bool TimelineQuotaWindowsOverlap(CodexQuotaSnapshot first, CodexQuotaSnapshot second)
    {
        return SameTimelineQuotaReset(first.FiveHourResetAtLocal, second.FiveHourResetAtLocal) ||
               SameTimelineQuotaReset(first.WeekResetAtLocal, second.WeekResetAtLocal);
    }

    private static bool SameTimelineQuotaReset(DateTimeOffset? first, DateTimeOffset? second)
    {
        return first is not null && second is not null &&
               Math.Abs((first.Value - second.Value).TotalMinutes) <= 10;
    }

    private static decimal ClampTimelinePercent(decimal value)
    {
        return Math.Max(0m, Math.Min(100m, value));
    }

    public static IReadOnlyList<CodexQuotaSnapshot> ReadQuotaHistoryQuotaSnapshots(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal)
    {
        var historySnapshots = ReadQuotaHistorySnapshots(startLocal, endLocal)
            .Where(IsQuotaHistorySnapshot)
            .ToList();
        var historySparkTimes = historySnapshots
            .Where(IsGpt53QuotaSnapshot)
            .Select(item => item.TimestampLocal)
            .ToHashSet();

        return MergeQuotaSnapshots(historySnapshots
                .Where(item => !IsStaleGeneralSnapshotForLiveSpark(item, historySparkTimes))
                .Select(ToCodexQuotaSnapshot))
            .ToList();
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
        WarmQuotaTimelineDay(dayStart.AddDays(-1));
        WarmQuotaTimelineDay(dayStart);
        WarmQuotaTimelineDay(dayStart.AddDays(1));
    }

    public static void WarmQuotaSnapshotDays(
        IEnumerable<DateTimeOffset> daysLocal,
        CancellationToken cancellationToken = default)
    {
        var days = daysLocal
            .Select(StartOfDay)
            .Distinct()
            .OrderBy(item => item)
            .ToList();
        if (days.Count == 0)
        {
            return;
        }

        var startLocal = days[0];
        var endLocal = days[^1].AddDays(1);
        var requestedDates = days
            .Select(item => DateOnly.FromDateTime(item.DateTime))
            .ToHashSet();

        cancellationToken.ThrowIfCancellationRequested();
        var liveSnapshots = ReadRateLimitSnapshots(startLocal, endLocal);
        var liveQuotaSnapshots = liveSnapshots
            .Where(IsQuotaHistorySnapshot)
            .Select(ToCodexQuotaSnapshot)
            .ToList();
        var liveSparkTimes = liveQuotaSnapshots
            .Where(IsGpt53QuotaSnapshot)
            .Select(item => item.SnapshotLocal)
            .ToHashSet();
        var historyQuotaSnapshots = ReadQuotaHistorySnapshots(startLocal, endLocal)
            .Where(IsQuotaHistorySnapshot)
            .Where(item => !IsStaleGeneralSnapshotForLiveSpark(item, liveSparkTimes))
            .Select(ToCodexQuotaSnapshot);
        var scannedByDate = MergeQuotaSnapshots(liveQuotaSnapshots.Concat(historyQuotaSnapshots))
            .Where(item => requestedDates.Contains(DateOnly.FromDateTime(item.SnapshotLocal.DateTime)))
            .GroupBy(item => DateOnly.FromDateTime(item.SnapshotLocal.DateTime))
            .ToDictionary(group => group.Key, group => (IReadOnlyList<CodexQuotaSnapshot>)group.ToList());

        var cache = QuotaSnapshotCacheStore.Load(CacheFolder);
        foreach (var day in days)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var date = DateOnly.FromDateTime(day.DateTime);
            var existing = cache.GetSnapshots(date);
            scannedByDate.TryGetValue(date, out var scanned);
            var merged = MergeQuotaSnapshots(existing.Concat(scanned ?? Array.Empty<CodexQuotaSnapshot>())).ToList();
            cache.Put(date, merged, isComplete: true, scannedThroughLocal: day.AddDays(1).AddTicks(-1));
        }

        cache.Save();
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
            var hasAmbiguousQuotaCache = HasAmbiguousCodexQuotaCache(daySnapshots);
            var fullHistoricalDay = dayStart < todayStart;
            var hasCompleteCoverage =
                hasRecord && record is not null &&
                (fullHistoricalDay && record.IsComplete ||
                 daySnapshots.Count > 0 &&
                 !hasPrefixGap &&
                 !hasAmbiguousQuotaCache &&
                 (record.IsComplete ||
                  effectiveScannedThrough is not null && effectiveScannedThrough.Value >= effectiveClippedEnd.AddTicks(-1)));

            if (!hasCompleteCoverage)
            {
                DateTimeOffset scanStart;
                if (hasPrefixGap)
                {
                    scanStart = clippedStart;
                }
                else if (hasAmbiguousQuotaCache)
                {
                    scanStart = dayStart;
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

    private static bool HasAmbiguousCodexQuotaCache(IReadOnlyList<CodexQuotaSnapshot> daySnapshots)
    {
        var resetGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var snapshot in daySnapshots)
        {
            if (!string.Equals(snapshot.LimitId, "codex", StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrWhiteSpace(snapshot.LimitName) ||
                snapshot.WeekResetAtLocal is not { } resetAt)
            {
                continue;
            }

            resetGroups.Add(resetAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
            if (resetGroups.Count > 1)
            {
                return true;
            }
        }

        return false;
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

        var liveQuotaSnapshots = liveSnapshots
            .Where(IsQuotaHistorySnapshot)
            .Select(ToCodexQuotaSnapshot)
            .ToList();
        var liveSparkTimes = liveQuotaSnapshots
            .Where(IsGpt53QuotaSnapshot)
            .Select(item => item.SnapshotLocal)
            .ToHashSet();
        var historyQuotaSnapshots = ReadQuotaHistorySnapshots(startLocal, endLocal)
            .Where(IsQuotaHistorySnapshot)
            .Where(item => !IsStaleGeneralSnapshotForLiveSpark(item, liveSparkTimes))
            .Select(ToCodexQuotaSnapshot);

        return MergeQuotaSnapshots(liveQuotaSnapshots
                .Concat(historyQuotaSnapshots))
            .ToList();
    }

    private static bool IsStaleGeneralSnapshotForLiveSpark(
        RateLimitSnapshot snapshot,
        ISet<DateTimeOffset> liveSparkTimes)
    {
        return liveSparkTimes.Contains(snapshot.TimestampLocal) &&
               string.Equals(snapshot.LimitId, "codex", StringComparison.OrdinalIgnoreCase) &&
               string.IsNullOrWhiteSpace(snapshot.LimitName);
    }

    private static bool IsStaleGeneralSnapshotForSpark(
        CodexQuotaSnapshot snapshot,
        ISet<DateTimeOffset> sparkTimes)
    {
        return sparkTimes.Contains(snapshot.SnapshotLocal) &&
               string.Equals(snapshot.LimitId, "codex", StringComparison.OrdinalIgnoreCase) &&
               string.IsNullOrWhiteSpace(snapshot.LimitName);
    }

    private static IEnumerable<CodexQuotaSnapshot> MergeQuotaSnapshots(IEnumerable<CodexQuotaSnapshot> snapshots)
    {
        return snapshots
            .Select(NormalizeQuotaSnapshotWindows)
            .GroupBy(item => $"{item.SnapshotLocal:O}|{NormalizeLimitId(item.LimitId)}", StringComparer.OrdinalIgnoreCase)
            .Select(SelectBestQuotaSnapshot)
            .OrderBy(item => item.SnapshotLocal);
    }

    internal static CodexQuotaSnapshot NormalizeQuotaSnapshotWindows(CodexQuotaSnapshot snapshot)
    {
        // During the temporary removal of the 5h limit, Codex emits the 7d
        // window as `primary` and omits `secondary`. Older builds persisted
        // that payload in the 5h columns. A real 5h reset cannot be more than
        // one day after its snapshot, so repair those cached rows on read.
        if (snapshot.FiveHourUsedPercent is not null &&
            snapshot.WeekUsedPercent is null &&
            snapshot.FiveHourResetAtLocal is { } resetAt)
        {
            var resetDistance = resetAt - snapshot.SnapshotLocal;
            if (resetDistance > TimeSpan.FromDays(1) && resetDistance <= TimeSpan.FromDays(8))
            {
                snapshot = snapshot with
                {
                    FiveHourUsedPercent = null,
                    FiveHourResetAtLocal = null,
                    WeekUsedPercent = snapshot.FiveHourUsedPercent,
                    WeekResetAtLocal = resetAt
                };
            }
            else if (resetDistance > TimeSpan.FromDays(8))
            {
                snapshot = snapshot with
                {
                    FiveHourUsedPercent = null,
                    FiveHourResetAtLocal = null
                };
            }
        }

        if (snapshot.WeekResetAtLocal is { } weekReset &&
            (weekReset < snapshot.SnapshotLocal.AddMinutes(-10) ||
             weekReset - snapshot.SnapshotLocal > TimeSpan.FromDays(8)))
        {
            snapshot = snapshot with
            {
                WeekUsedPercent = null,
                WeekResetAtLocal = null
            };
        }

        return snapshot;
    }

    private static CodexQuotaSnapshot SelectBestQuotaSnapshot(IEnumerable<CodexQuotaSnapshot> snapshots)
    {
        return snapshots
            .OrderByDescending(item => item.WeekUsedPercent ?? -1m)
            .ThenByDescending(item => item.FiveHourUsedPercent ?? -1m)
            .ThenByDescending(GetQuotaSnapshotCompleteness)
            .ThenByDescending(item => item.SnapshotLocal)
            .First();
    }

    private static int GetQuotaSnapshotCompleteness(CodexQuotaSnapshot snapshot)
    {
        var score = 0;
        if (!string.IsNullOrWhiteSpace(snapshot.LimitId))
        {
            score++;
        }

        if (!string.IsNullOrWhiteSpace(snapshot.LimitName))
        {
            score++;
        }

        if (snapshot.FiveHourUsedPercent is not null)
        {
            score++;
        }

        if (snapshot.FiveHourResetAtLocal is not null)
        {
            score++;
        }

        if (snapshot.WeekUsedPercent is not null)
        {
            score++;
        }

        if (snapshot.WeekResetAtLocal is not null)
        {
            score++;
        }

        return score;
    }

    private static CodexQuotaEstimate BuildQuotaEstimate(RateLimitSnapshot snapshot, DateTimeOffset now)
    {
        return new CodexQuotaEstimate(
            snapshot.TimestampLocal,
            snapshot.LimitId,
            snapshot.LimitName,
            BuildQuotaWindowEstimate("5h", snapshot.FiveHour, now),
            BuildQuotaWindowEstimate("1周", snapshot.Week, now));
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
        var usedCost = usage.EstimateCost(PriceProfiles.PrimaryCodex);
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
                modelContextWindow = snapshot.ModelContextWindow,
                fiveHour = ToQuotaHistoryWindow(snapshot.FiveHour),
                week = ToQuotaHistoryWindow(snapshot.Week)
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

            var limitId = GetString(root, "limitId");
            var limitName = GetString(root, "limitName");
            var modelContextWindow = GetInt64(root, "modelContextWindow");
            NormalizeRateLimitIdentity(modelContextWindow, ref limitId, ref limitName);

            var windows = ClassifyRateLimitWindows(
                TryReadQuotaHistoryWindow(root, "fiveHour"),
                TryReadQuotaHistoryWindow(root, "week"));

            return new RateLimitSnapshot(
                timestamp,
                limitId,
                limitName,
                windows.FiveHour,
                windows.Week,
                modelContextWindow);
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
                    var newEvents = ReadEventsUncached(detailStart, detailEnd, useLiveCursor: true);
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
                    var newEvents = ReadEventsUncached(scanStart, effectiveEndLocal, useLiveCursor: true);
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

    private static List<TokenUsageEvent> ReadEventsUncached(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        bool useLiveCursor = false)
    {
        var events = new List<TokenUsageEvent>();
        var incremental = useLiveCursor && IsLiveRange(startLocal, endLocal);

        foreach (var root in GetLogRoots())
        {
            foreach (var file in EnumerateJsonlFiles(root, startLocal))
            {
                if (incremental)
                {
                    ReadEventFileIncremental(file, startLocal, endLocal, events);
                }
                else
                {
                    ReadEventFile(file, startLocal, endLocal, events);
                    if (IsLiveRange(startLocal, endLocal))
                    {
                        UsageTailReader.Prime(file, startLocal);
                    }
                }
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
            var replayFilter = new SubagentReplayFilter();
            while (reader.ReadLine() is { } line)
            {
                if (!replayFilter.ShouldReadTokenCount(line))
                {
                    continue;
                }

                var usageEvent = TryReadUsageEvent(line, startLocal, endLocal);
                if (usageEvent is not null)
                {
                    events.Add(usageEvent);
                }
            }

            UsageReplayFilters[file] = replayFilter;
        }
        catch
        {
            return;
        }
    }

    private static void ReadEventFileIncremental(
        string file,
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        List<TokenUsageEvent> events)
    {
        var replayFilter = UsageReplayFilters.GetOrAdd(file, static _ => new SubagentReplayFilter());
        UsageTailReader.ReadNewLines(file, startLocal, line =>
        {
            if (!replayFilter.ShouldReadTokenCount(line))
            {
                return;
            }

            var usageEvent = TryReadUsageEvent(line, startLocal, endLocal);
            if (usageEvent is not null)
            {
                events.Add(usageEvent);
            }
        });
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
        target.MergeFrom(source);
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
        var incremental = IsLiveRange(startLocal, endLocal);
        foreach (var root in GetLogRoots())
        {
            foreach (var file in EnumerateJsonlFiles(root, startLocal))
            {
                if (incremental)
                {
                    ReadRateLimitFileIncremental(file, startLocal, endLocal, snapshots);
                    continue;
                }

                try
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream);
                    var replayFilter = new SubagentReplayFilter();
                    while (reader.ReadLine() is { } line)
                    {
                        if (!replayFilter.ShouldReadTokenCount(line) ||
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

                    QuotaReplayFilters[file] = replayFilter;
                }
                catch
                {
                    // Ignore files that are actively being written or are not readable.
                }
            }
        }

        return snapshots;
    }

    private static void ReadRateLimitFileIncremental(
        string file,
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        List<RateLimitSnapshot> snapshots)
    {
        var replayFilter = QuotaReplayFilters.GetOrAdd(file, static _ => new SubagentReplayFilter());
        QuotaTailReader.ReadNewLines(file, startLocal, line =>
        {
            if (!replayFilter.ShouldReadTokenCount(line) ||
                !line.Contains("\"rate_limits\"", StringComparison.Ordinal))
            {
                return;
            }

            var snapshot = TryReadRateLimitSnapshot(line, startLocal, endLocal);
            if (snapshot is not null)
            {
                snapshots.Add(snapshot);
            }
        });
    }

    private static bool IsLiveRange(DateTimeOffset startLocal, DateTimeOffset endLocal)
    {
        var now = DateTimeOffset.UtcNow.ToOffset(BeijingOffset);
        return startLocal >= StartOfDay(now) && endLocal <= now.AddMinutes(10);
    }

    private static void ResetLiveFileCursors()
    {
        UsageTailReader.Reset();
        QuotaTailReader.Reset();
        UsageReplayFilters.Clear();
        QuotaReplayFilters.Clear();
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

    private static bool IsGpt53QuotaSnapshot(CodexQuotaSnapshot snapshot)
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

    private static void NormalizeRateLimitIdentity(long modelContextWindow, ref string? limitId, ref string? limitName)
    {
        if (IsGpt53QuotaSnapshot(limitId, limitName))
        {
            return;
        }

        if (modelContextWindow > 0 &&
            modelContextWindow <= SparkContextWindowUpperBound &&
            string.Equals(limitId, "codex", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(limitName))
        {
            limitId = SparkLimitId;
            limitName = SparkLimitName;
        }
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
            snapshot.FiveHour?.UsedPercent,
            snapshot.FiveHour?.ResetAtLocal,
            snapshot.Week?.UsedPercent,
            snapshot.Week?.ResetAtLocal);
    }

    private static RateLimitSnapshot ToRateLimitSnapshot(CodexQuotaSnapshot snapshot)
    {
        return new RateLimitSnapshot(
            snapshot.SnapshotLocal,
            snapshot.LimitId,
            snapshot.LimitName,
            ToRateLimitWindow(snapshot.FiveHourUsedPercent, FiveHourWindowMinutes, snapshot.FiveHourResetAtLocal),
            ToRateLimitWindow(snapshot.WeekUsedPercent, WeeklyWindowMinutes, snapshot.WeekResetAtLocal),
            0);
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

            var windows = ClassifyRateLimitWindows(primary, secondary);

            var modelContextWindow = payload.TryGetProperty("info", out var info)
                ? GetInt64(info, "model_context_window")
                : 0;
            var limitId = GetString(rateLimits, "limit_id");
            var limitName = GetString(rateLimits, "limit_name");
            NormalizeRateLimitIdentity(modelContextWindow, ref limitId, ref limitName);

            return new RateLimitSnapshot(
                timestamp,
                limitId,
                limitName,
                windows.FiveHour,
                windows.Week,
                modelContextWindow);
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

    private static (RateLimitWindowSnapshot? FiveHour, RateLimitWindowSnapshot? Week) ClassifyRateLimitWindows(
        RateLimitWindowSnapshot? first,
        RateLimitWindowSnapshot? second)
    {
        var windows = new[] { first, second }
            .Where(window => window is not null)
            .Select(window => window!)
            .GroupBy(window => window.WindowMinutes)
            .Select(group => group.First())
            .ToList();

        // Field position is not stable, but window duration is. Other windows
        // (notably the 30-day reset-card window) are not 5h/7d quota data.
        return (
            windows.FirstOrDefault(window => IsFiveHourWindow(window.WindowMinutes)),
            windows.FirstOrDefault(window => IsWeeklyWindow(window.WindowMinutes)));
    }

    internal static bool IsFiveHourWindow(int windowMinutes) => windowMinutes == FiveHourWindowMinutes;

    internal static bool IsWeeklyWindow(int windowMinutes) => windowMinutes == WeeklyWindowMinutes;

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
            var replayFilter = new SubagentReplayFilter();
            while (reader.ReadLine() is { } line)
            {
                if (!replayFilter.ShouldReadTokenCount(line))
                {
                    continue;
                }

                ReadLine(line, startLocal, endLocal, summary, dailyBuckets);
            }

            UsageReplayFilters[file] = replayFilter;
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
