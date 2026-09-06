namespace CodexTokenMonitor;

internal sealed class UsageCacheStore
{
    private const string CacheFileName = "token-cache-v3.sqlite3";
    private const string CurrentQuotaHistoryFileName = "quota-history-v2.jsonl";
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

    private UsageCacheStore(string cachePath)
    {
        this.cachePath = cachePath;
        available = InitializeDatabase();
        if (available)
        {
            DeleteLegacyDerivedFiles(cachePath);
        }
    }

    public static string GetCachePath(string folderName)
    {
        return Path.Combine(MonitorCachePaths.LocalAppData, folderName,
            folderName == "CodexTokenMonitor" ? "token-cache-v4.sqlite3" : CacheFileName);
    }

    public static bool Delete(string folderName)
    {
        var cachePath = GetCachePath(folderName);
        Stores.TryRemove(cachePath, out _);
        QuotaSnapshotCacheStore.Forget(folderName);
        SqliteConnection.ClearAllPools();
        var deleted = false;
        var directory = Path.GetDirectoryName(cachePath) ?? "";
        foreach (var path in new[] { cachePath, cachePath + "-wal", cachePath + "-shm" }
                     .Concat(LegacyDerivedFileNames.Select(name => Path.Combine(directory, name)))
                     .Append(Path.Combine(directory, CurrentQuotaHistoryFileName)))
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

    private static void DeleteLegacyDerivedFiles(string cachePath)
    {
        var directory = Path.GetDirectoryName(cachePath) ?? "";
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
        DateTimeOffset endInclusive,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Load(folderName).ReadIncompleteDays(startInclusive, endInclusive, cancellationToken);
    }

    private IReadOnlyList<DateTimeOffset> ReadIncompleteDays(
        DateTimeOffset startInclusive,
        DateTimeOffset endInclusive,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = new List<DateTimeOffset>();
        var start = StartOfDay(startInclusive);
        var end = StartOfDay(endInclusive);
        if (start > end)
        {
            return result;
        }

        if (!available)
        {
            return EnumerateDaysDescending(start, end, cancellationToken);
        }

        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT date, is_complete, events, input_tokens, cached_input_tokens,
                       uncached_input_tokens, output_tokens, reasoning_output_tokens, total_tokens,
                       long_context_events, long_context_input_tokens, long_context_cached_input_tokens,
                       long_context_output_tokens, peak_input_tokens, peak_cached_input_tokens, peak_output_tokens,
                       cache_write_input_tokens, long_context_cache_write_input_tokens,
                       peak_cache_write_input_tokens,
                       (
                           SELECT COUNT(*)
                           FROM usage_events detail
                           WHERE detail.date = usage_days.date
                       ) AS detail_event_count, model_usage_json
                FROM usage_days
                WHERE date >= $start_date AND date <= $end_date
                """;
            command.Parameters.AddWithValue("$start_date", DateKey(DateOnly.FromDateTime(start.DateTime)));
            command.Parameters.AddWithValue("$end_date", DateKey(DateOnly.FromDateTime(end.DateTime)));

            var records = new Dictionary<string, (bool IsComplete, bool IsValid, long Events, long DetailEventCount)>(
                StringComparer.Ordinal);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var cachedBucket = ReadCachedBucketCounters(reader, 2);
                records[reader.GetString(0)] = (
                    reader.GetInt64(1) != 0,
                    IsNormalizedBucket(cachedBucket),
                    cachedBucket.Events,
                    reader.GetInt64(19));
            }

            for (var day = end; day >= start; day = day.AddDays(-1))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var dateKey = DateKey(DateOnly.FromDateTime(day.DateTime));
                if (!records.TryGetValue(dateKey, out var record) ||
                    !record.IsComplete ||
                    !record.IsValid ||
                    record.DetailEventCount != record.Events)
                {
                    result.Add(day);
                }
            }

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Preserve the previous fail-safe behavior: an unreadable cache is incomplete.
            return EnumerateDaysDescending(start, end, cancellationToken);
        }
    }

    private static List<DateTimeOffset> EnumerateDaysDescending(
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken = default)
    {
        var result = new List<DateTimeOffset>();
        for (var day = end; day >= start; day = day.AddDays(-1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(day);
        }

        return result;
    }

    private static DateTimeOffset StartOfDay(DateTimeOffset value)
    {
        var local = value.ToOffset(CodexUsageReader.BeijingOffset);
        return new DateTimeOffset(local.Year, local.Month, local.Day, 0, 0, 0, CodexUsageReader.BeijingOffset);
    }

    public static UsageCacheStore Load()
    {
        return Load("CodexTokenMonitor");
    }

    public static UsageCacheStore Load(string folderName)
    {
        var cachePath = GetCachePath(folderName);
        return Stores.GetOrAdd(
            cachePath,
            static path => new Lazy<UsageCacheStore>(
                () => new UsageCacheStore(path),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    public bool TryGet(DateOnly date, out TokenUsageBucket bucket)
    {
        bucket = new TokenUsageBucket
        {
            StartLocal = new DateTimeOffset(date.Year, date.Month, date.Day, 0, 0, 0, CodexUsageReader.BeijingOffset)
        };

        if (!TryGetRecord(date, out var record) || !record.IsValid)
        {
            return false;
        }

        bucket.ModelUsage = record.ModelUsage;
        bucket.Events = record.Events;
        bucket.InputTokens = record.InputTokens;
        bucket.CachedInputTokens = record.CachedInputTokens;
        bucket.CacheWriteInputTokens = record.CacheWriteInputTokens;
        bucket.UncachedInputTokens = record.UncachedInputTokens;
        bucket.OutputTokens = record.OutputTokens;
        bucket.ReasoningOutputTokens = record.ReasoningOutputTokens;
        bucket.TotalTokens = record.TotalTokens;
        bucket.LongContextEvents = record.LongContextEvents;
        bucket.LongContextInputTokens = record.LongContextInputTokens;
        bucket.LongContextCachedInputTokens = record.LongContextCachedInputTokens;
        bucket.LongContextCacheWriteInputTokens = record.LongContextCacheWriteInputTokens;
        bucket.LongContextOutputTokens = record.LongContextOutputTokens;
        bucket.PeakInputTokens = record.PeakInputTokens;
        bucket.PeakCachedInputTokens = record.PeakCachedInputTokens;
        bucket.PeakCacheWriteInputTokens = record.PeakCacheWriteInputTokens;
        bucket.PeakOutputTokens = record.PeakOutputTokens;
        bucket.LastTokenEventLocal = record.LastTokenEventLocal;
        bucket.NormalizeInPlace();
        return true;
    }

    public IReadOnlyList<string> GetModelIds()
    {
        if (!available) return Array.Empty<string>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT model_id FROM usage_events WHERE model_id IS NOT NULL AND model_id <> '' ORDER BY model_id";
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    public TokenUsageSummary ReadRange(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        startLocal = startLocal.ToOffset(CodexUsageReader.BeijingOffset);
        endLocal = endLocal.ToOffset(CodexUsageReader.BeijingOffset);
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
                cancellationToken.ThrowIfCancellationRequested();
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
                    if (TryGetComplete(connection, date, out var cachedBucket))
                    {
                        AddBucketToSummary(summary, dailyBuckets, cachedBucket);
                        continue;
                    }
                }

                foreach (var usageEvent in ReadDetailEvents(connection, clippedStart, clippedEnd, cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AddEventToSummary(summary, dailyBuckets, usageEvent);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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

    private static bool TryGetComplete(SqliteConnection connection, DateOnly date, out TokenUsageBucket bucket)
    {
        bucket = new TokenUsageBucket
        {
            StartLocal = new DateTimeOffset(date.Year, date.Month, date.Day, 0, 0, 0, CodexUsageReader.BeijingOffset)
        };
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT is_complete, events, input_tokens, cached_input_tokens, uncached_input_tokens,
                   output_tokens, reasoning_output_tokens, total_tokens,
                   long_context_events, long_context_input_tokens,
                   long_context_cached_input_tokens, long_context_output_tokens,
                   peak_input_tokens, peak_cached_input_tokens, peak_output_tokens,
                   cache_write_input_tokens, long_context_cache_write_input_tokens,
                   peak_cache_write_input_tokens,
                   last_token_event_local,
                   (SELECT COUNT(*) FROM usage_events WHERE usage_events.date = usage_days.date), model_usage_json
            FROM usage_days
            WHERE date = $date
            """;
        command.Parameters.AddWithValue("$date", DateKey(date));
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetInt64(0) == 0)
        {
            return false;
        }

        bucket = ReadCachedBucketCounters(reader, 1, bucket.StartLocal);
        bucket.LastTokenEventLocal = ReadDateTimeOffset(reader, 18);
        var detailEventCount = reader.GetInt64(19);
        bucket.ModelUsage = ReadModelUsage(reader, 20);
        if (!IsNormalizedBucket(bucket) ||
            detailEventCount != bucket.Events)
        {
            return false;
        }

        bucket.NormalizeInPlace();
        return true;
    }

    public IReadOnlyList<TokenUsageBucket> ReadDetailRows(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        startLocal = startLocal.ToOffset(CodexUsageReader.BeijingOffset);
        endLocal = endLocal.ToOffset(CodexUsageReader.BeijingOffset);
        if (!available || startLocal >= endLocal)
        {
            return Array.Empty<TokenUsageBucket>();
        }

        try
        {
            using var connection = OpenConnection();
            return ToDetailBuckets(ReadDetailEvents(connection, startLocal, endLocal, cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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
                       long_context_output_tokens, peak_input_tokens, peak_cached_input_tokens, peak_output_tokens,
                       cache_write_input_tokens, long_context_cache_write_input_tokens,
                       peak_cache_write_input_tokens,
                       (SELECT COUNT(*) FROM usage_events WHERE usage_events.date = usage_days.date) AS detail_event_count, model_usage_json
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
                PeakInputTokens = reader.GetInt64(15),
                PeakCachedInputTokens = reader.GetInt64(16),
                PeakOutputTokens = reader.GetInt64(17),
                CacheWriteInputTokens = reader.GetInt64(18),
                LongContextCacheWriteInputTokens = reader.GetInt64(19),
                PeakCacheWriteInputTokens = reader.GetInt64(20),
                DetailEventCount = reader.GetInt32(21),
                ModelUsage = ReadModelUsage(reader, 22)
            };
            var bucket = ToBucket(record);
            record.IsValid = IsNormalizedBucket(bucket);
            bucket.NormalizeInPlace();
            CopyBucketCounters(bucket, record);
            if (!record.IsValid)
            {
                record.IsComplete = false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    public IReadOnlyList<TokenUsageEvent> GetDetailEvents(
        DateOnly date,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!available)
        {
            return Array.Empty<TokenUsageEvent>();
        }

        try
        {
            using var connection = OpenConnection();
            return ReadDetailEvents(connection, date, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Array.Empty<TokenUsageEvent>();
        }
    }

    public IReadOnlyList<TokenUsageEvent> GetDetailEvents(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        startLocal = startLocal.ToOffset(CodexUsageReader.BeijingOffset);
        endLocal = endLocal.ToOffset(CodexUsageReader.BeijingOffset);
        if (!available || startLocal >= endLocal)
        {
            return Array.Empty<TokenUsageEvent>();
        }

        try
        {
            var queryStart = StartOfDay(startLocal).AddDays(-1);
            var queryEnd = StartOfDay(endLocal).AddDays(1);
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT timestamp_local, input_tokens, cached_input_tokens, output_tokens,
                       reasoning_output_tokens, total_tokens, event_key, cache_write_input_tokens, model_id, service_tier
                FROM usage_events
                WHERE date >= $start_date AND date <= $end_date
                ORDER BY timestamp_local
                """;
            command.Parameters.AddWithValue("$start_date", DateKey(DateOnly.FromDateTime(queryStart.DateTime)));
            command.Parameters.AddWithValue("$end_date", DateKey(DateOnly.FromDateTime(queryEnd.DateTime)));

            var result = new List<TokenUsageEvent>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var timestampLocal = ParseDateTimeOffset(reader.GetString(0))
                    .ToOffset(CodexUsageReader.BeijingOffset);
                if (timestampLocal < startLocal || timestampLocal >= endLocal)
                {
                    continue;
                }

                result.Add(new TokenUsageEvent(
                    timestampLocal,
                    reader.GetInt64(1),
                    reader.GetInt64(2),
                    reader.GetInt64(3),
                    reader.GetInt64(4),
                    reader.GetInt64(5),
                    reader.GetString(6),
                    reader.GetInt64(7), reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9)));
            }

            return UsageEventMerger.Merge(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Array.Empty<TokenUsageEvent>();
        }
    }

    public IReadOnlyList<TokenUsageEvent> GetAllDetailEvents()
    {
        if (!available)
        {
            return Array.Empty<TokenUsageEvent>();
        }

        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT event_key, timestamp_local, input_tokens, cached_input_tokens,
                       output_tokens, reasoning_output_tokens, total_tokens, cache_write_input_tokens, model_id, service_tier
                FROM usage_events
                ORDER BY timestamp_local
                """;

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
                    reader.GetString(0),
                    reader.GetInt64(7), reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9)));
            }

            return UsageEventMerger.Merge(result);
        }
        catch
        {
            return Array.Empty<TokenUsageEvent>();
        }
    }

    /// <summary>
    /// Streams detail events directly from SQLite for transfer/export paths.
    /// Unlike <see cref="GetAllDetailEvents"/>, this does not materialize the
    /// complete event table in memory first.
    /// </summary>
    public IEnumerable<TokenUsageEvent> EnumerateDetailEvents(
        DateTimeOffset? startInclusive,
        DateTimeOffset? endExclusive,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!available ||
            (startInclusive is null) != (endExclusive is null) ||
            startInclusive >= endExclusive)
        {
            yield break;
        }

        var normalizedStart = startInclusive?.ToOffset(CodexUsageReader.BeijingOffset);
        var normalizedEnd = endExclusive?.ToOffset(CodexUsageReader.BeijingOffset);
        DateTimeOffset? queryStart = normalizedStart is null
            ? null
            : StartOfDay(normalizedStart.Value).AddDays(-1);
        DateTimeOffset? queryEnd = normalizedEnd is null
            ? null
            : StartOfDay(normalizedEnd.Value).AddDays(1);

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = normalizedStart is null
            ? """
                SELECT event_key, timestamp_local, input_tokens, cached_input_tokens,
                       output_tokens, reasoning_output_tokens, total_tokens, cache_write_input_tokens, model_id, service_tier
                FROM usage_events
                ORDER BY timestamp_local
                """
            : """
                SELECT event_key, timestamp_local, input_tokens, cached_input_tokens,
                       output_tokens, reasoning_output_tokens, total_tokens, cache_write_input_tokens, model_id, service_tier
                FROM usage_events
                WHERE date >= $start_date AND date <= $end_date
                ORDER BY timestamp_local
                """;
        if (queryStart is not null && queryEnd is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            command.Parameters.AddWithValue(
                "$start_date",
                DateKey(DateOnly.FromDateTime(queryStart.Value.DateTime)));
            command.Parameters.AddWithValue(
                "$end_date",
                DateKey(DateOnly.FromDateTime(queryEnd.Value.DateTime)));
        }

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var timestampLocal = ParseDateTimeOffset(reader.GetString(1))
                .ToOffset(CodexUsageReader.BeijingOffset);
            if (normalizedStart is not null &&
                (timestampLocal < normalizedStart.Value || timestampLocal >= normalizedEnd!.Value))
            {
                continue;
            }

            yield return new TokenUsageEvent(
                timestampLocal,
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetString(0),
                reader.GetInt64(7), reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9));
        }
    }

    public int MergeImportedDetailEvents(IReadOnlyList<TokenUsageEvent> importedEvents)
    {
        if (!available || importedEvents.Count == 0)
        {
            return 0;
        }

        MarkImportedUsageEventKeys(importedEvents);
        var added = 0;
        foreach (var group in importedEvents
                     .GroupBy(item => DateOnly.FromDateTime(item.Timestamp.ToOffset(CodexUsageReader.BeijingOffset).DateTime))
                     .OrderBy(item => item.Key))
        {
            var date = group.Key;
            var existing = GetDetailEvents(date);
            var existingKeys = existing
                .Select(UsageEventMerger.GetStableKey)
                .ToHashSet(StringComparer.Ordinal);
            var normalizedImports = group
                .Select(item => item with { Timestamp = item.Timestamp.ToOffset(CodexUsageReader.BeijingOffset) })
                .ToList();
            var merged = UsageEventMerger.Merge(existing.Concat(normalizedImports));
            added += merged.Count(item => !existingKeys.Contains(UsageEventMerger.GetStableKey(item)));

            var dayStart = new DateTimeOffset(
                date.Year,
                date.Month,
                date.Day,
                0,
                0,
                0,
                CodexUsageReader.BeijingOffset);
            var bucket = CreateBucketFromEvents(dayStart, merged);
            var hasRecord = TryGetRecord(date, out var record);
            Put(
                bucket,
                hasRecord && record.IsComplete,
                hasRecord ? record.ScannedThroughLocal : null,
                merged,
                replaceDetailEvents: true,
                propagateErrors: true);
        }

        return added;
    }

    public bool HasDetailEvents(
        DateOnly date,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
            cancellationToken.ThrowIfCancellationRequested();
            return command.ExecuteScalar() is not null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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
                deleteEventsCommand.CommandText = """
                    DELETE FROM usage_events
                    WHERE date = $date
                      AND event_key NOT IN (SELECT event_key FROM imported_usage_event_keys)
                    """;
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
        bool replaceDetailEvents = true,
        CancellationToken cancellationToken = default,
        bool propagateErrors = false)
    {
        if (!available)
        {
            return;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            bucket.NormalizeInPlace();
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
                        long_context_output_tokens, peak_input_tokens, peak_cached_input_tokens, peak_output_tokens,
                        cache_write_input_tokens, long_context_cache_write_input_tokens, peak_cache_write_input_tokens, model_usage_json
                    )
                    VALUES (
                        $date, $is_complete, $scanned_through_local, $events, $input_tokens, $cached_input_tokens,
                        $uncached_input_tokens, $output_tokens, $reasoning_output_tokens, $total_tokens, $last_token_event_local,
                        $long_context_events, $long_context_input_tokens, $long_context_cached_input_tokens,
                        $long_context_output_tokens, $peak_input_tokens, $peak_cached_input_tokens, $peak_output_tokens,
                        $cache_write_input_tokens, $long_context_cache_write_input_tokens, $peak_cache_write_input_tokens, $model_usage_json
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
                        long_context_output_tokens = excluded.long_context_output_tokens,
                        peak_input_tokens = excluded.peak_input_tokens,
                        peak_cached_input_tokens = excluded.peak_cached_input_tokens,
                        peak_output_tokens = excluded.peak_output_tokens,
                        cache_write_input_tokens = excluded.cache_write_input_tokens,
                        long_context_cache_write_input_tokens = excluded.long_context_cache_write_input_tokens,
                        peak_cache_write_input_tokens = excluded.peak_cache_write_input_tokens,
                        model_usage_json = excluded.model_usage_json
                    """;
                command.Parameters.AddWithValue("$date", key);
                command.Parameters.AddWithValue("$is_complete", isComplete ? 1 : 0);
                command.Parameters.AddWithValue("$model_usage_json", JsonSerializer.Serialize(bucket.ModelUsage));
                command.Parameters.AddWithValue("$scanned_through_local", ToDbValue(scannedThroughLocal));
                command.Parameters.AddWithValue("$events", bucket.Events);
                command.Parameters.AddWithValue("$input_tokens", bucket.InputTokens);
                command.Parameters.AddWithValue("$cached_input_tokens", bucket.CachedInputTokens);
                command.Parameters.AddWithValue("$cache_write_input_tokens", bucket.CacheWriteInputTokens);
                command.Parameters.AddWithValue("$uncached_input_tokens", bucket.UncachedInputTokens);
                command.Parameters.AddWithValue("$output_tokens", bucket.OutputTokens);
                command.Parameters.AddWithValue("$reasoning_output_tokens", bucket.ReasoningOutputTokens);
                command.Parameters.AddWithValue("$total_tokens", bucket.TotalTokens);
                command.Parameters.AddWithValue("$last_token_event_local", ToDbValue(bucket.LastTokenEventLocal));
                command.Parameters.AddWithValue("$long_context_events", bucket.LongContextEvents);
                command.Parameters.AddWithValue("$long_context_input_tokens", bucket.LongContextInputTokens);
                command.Parameters.AddWithValue("$long_context_cached_input_tokens", bucket.LongContextCachedInputTokens);
                command.Parameters.AddWithValue(
                    "$long_context_cache_write_input_tokens",
                    bucket.LongContextCacheWriteInputTokens);
                command.Parameters.AddWithValue("$long_context_output_tokens", bucket.LongContextOutputTokens);
                command.Parameters.AddWithValue("$peak_input_tokens", bucket.PeakInputTokens);
                command.Parameters.AddWithValue("$peak_cached_input_tokens", bucket.PeakCachedInputTokens);
                command.Parameters.AddWithValue("$peak_cache_write_input_tokens", bucket.PeakCacheWriteInputTokens);
                command.Parameters.AddWithValue("$peak_output_tokens", bucket.PeakOutputTokens);
                command.ExecuteNonQuery();
            }

            if (detailEvents is not null)
            {
                if (replaceDetailEvents)
                {
                    using var deleteCommand = connection.CreateCommand();
                    deleteCommand.Transaction = transaction;
                    deleteCommand.CommandText = """
                        DELETE FROM usage_events
                        WHERE date = $date
                          AND event_key NOT IN (SELECT event_key FROM imported_usage_event_keys)
                        """;
                    deleteCommand.Parameters.AddWithValue("$date", key);
                    deleteCommand.ExecuteNonQuery();
                }

                using var insertCommand = connection.CreateCommand();
                insertCommand.Transaction = transaction;
                insertCommand.CommandText = """
                    INSERT OR REPLACE INTO usage_events (
                        date, event_key, timestamp_local, input_tokens, cached_input_tokens,
                        output_tokens, reasoning_output_tokens, total_tokens, cache_write_input_tokens, model_id, service_tier
                    )
                    VALUES (
                        $date, $event_key, $timestamp_local, $input_tokens, $cached_input_tokens,
                        $output_tokens, $reasoning_output_tokens, $total_tokens, $cache_write_input_tokens, $model_id, $service_tier
                    )
                    """;
                insertCommand.Parameters.AddWithValue("$date", key);
                insertCommand.Parameters.AddWithValue("$event_key", "");
                insertCommand.Parameters.AddWithValue("$model_id", DBNull.Value);
                insertCommand.Parameters.AddWithValue("$service_tier", DBNull.Value);
                insertCommand.Parameters.AddWithValue("$timestamp_local", "");
                insertCommand.Parameters.AddWithValue("$input_tokens", 0L);
                insertCommand.Parameters.AddWithValue("$cached_input_tokens", 0L);
                insertCommand.Parameters.AddWithValue("$cache_write_input_tokens", 0L);
                insertCommand.Parameters.AddWithValue("$output_tokens", 0L);
                insertCommand.Parameters.AddWithValue("$reasoning_output_tokens", 0L);
                insertCommand.Parameters.AddWithValue("$total_tokens", 0L);
                foreach (var item in UsageEventMerger.Merge(detailEvents))
                {
                    insertCommand.Parameters["$event_key"].Value = BuildUsageEventKey(item);
                    insertCommand.Parameters["$model_id"].Value = (object?)item.ModelId ?? DBNull.Value;
                    insertCommand.Parameters["$service_tier"].Value = (object?)item.ServiceTier ?? DBNull.Value;
                    insertCommand.Parameters["$timestamp_local"].Value = FormatDateTimeOffset(item.Timestamp);
                    insertCommand.Parameters["$input_tokens"].Value = item.InputTokens;
                    insertCommand.Parameters["$cached_input_tokens"].Value = item.CachedInputTokens;
                    insertCommand.Parameters["$cache_write_input_tokens"].Value = item.CacheWriteInputTokens;
                    insertCommand.Parameters["$output_tokens"].Value = item.OutputTokens;
                    insertCommand.Parameters["$reasoning_output_tokens"].Value = item.ReasoningOutputTokens;
                    insertCommand.Parameters["$total_tokens"].Value = item.TotalTokens;
                    insertCommand.ExecuteNonQuery();
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception) when (!propagateErrors)
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
                    long_context_output_tokens INTEGER NOT NULL DEFAULT 0,
                    peak_input_tokens INTEGER NOT NULL DEFAULT 0,
                    peak_cached_input_tokens INTEGER NOT NULL DEFAULT 0,
                    peak_output_tokens INTEGER NOT NULL DEFAULT 0,
                    cache_write_input_tokens INTEGER NOT NULL DEFAULT 0,
                    long_context_cache_write_input_tokens INTEGER NOT NULL DEFAULT 0,
                    peak_cache_write_input_tokens INTEGER NOT NULL DEFAULT 0,
                    model_usage_json TEXT NOT NULL DEFAULT '{}'
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
                    cache_write_input_tokens INTEGER NOT NULL DEFAULT 0,
                    model_id TEXT NULL,
                    service_tier TEXT NULL,
                    PRIMARY KEY (date, event_key)
                )
                """);
            ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS idx_usage_events_time ON usage_events(timestamp_local)");
            ExecuteNonQuery(connection, """
                CREATE TABLE IF NOT EXISTS imported_usage_event_keys (
                    event_key TEXT PRIMARY KEY
                )
                """);
            EnsureLongContextColumns(connection);
            EnsurePeakPricingColumns(connection);
            EnsureCacheWriteColumns(connection);
            EnsureModelColumns(connection);
            if (Path.GetFileName(cachePath) == "token-cache-v4.sqlite3")
            {
                // Re-read only affected days once after fixing repeated session_meta.
                // Keep imported events; the normal merge enriches matching keys.
                ExecuteNonQuery(connection, """
                    CREATE TABLE IF NOT EXISTS cache_maintenance (name TEXT PRIMARY KEY);
                    UPDATE usage_days SET is_complete = 0
                    WHERE date IN (SELECT date FROM usage_events WHERE model_id IS NULL OR model_id = '')
                      AND NOT EXISTS (SELECT 1 FROM cache_maintenance WHERE name = 'model-context-v2');
                    INSERT OR IGNORE INTO cache_maintenance VALUES ('model-context-v2');
                    UPDATE usage_days SET is_complete = 0
                    WHERE events > 0 AND NOT EXISTS (SELECT 1 FROM cache_maintenance WHERE name = 'service-tier-v1');
                    INSERT OR IGNORE INTO cache_maintenance VALUES ('service-tier-v1');
                    """);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void EnsureModelColumns(SqliteConnection connection)
    {
        var eventColumns = ReadTableColumns(connection, "usage_events");
        if (!eventColumns.Contains("model_id"))
            ExecuteNonQuery(connection, "ALTER TABLE usage_events ADD COLUMN model_id TEXT NULL");
        if (!eventColumns.Contains("service_tier"))
            ExecuteNonQuery(connection, "ALTER TABLE usage_events ADD COLUMN service_tier TEXT NULL");
        var dayColumns = ReadTableColumns(connection, "usage_days");
        if (!dayColumns.Contains("model_usage_json"))
            ExecuteNonQuery(connection, "ALTER TABLE usage_days ADD COLUMN model_usage_json TEXT NOT NULL DEFAULT '{}'");
    }

    private static Dictionary<string, TokenUsageBucket> ReadModelUsage(SqliteDataReader reader, int ordinal)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, TokenUsageBucket>>(reader.GetString(ordinal)) ?? new();
        }
        catch (JsonException) { return new(); }
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

    private static void EnsurePeakPricingColumns(SqliteConnection connection)
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
        foreach (var column in new[] { "peak_input_tokens", "peak_cached_input_tokens", "peak_output_tokens" })
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

        const string peakPredicate = "((substr(e.timestamp_local, 12, 5) >= '09:00' AND substr(e.timestamp_local, 12, 5) < '12:00') OR (substr(e.timestamp_local, 12, 5) >= '14:00' AND substr(e.timestamp_local, 12, 5) < '18:00'))";
        ExecuteNonQuery(connection, $"""
            UPDATE usage_days
            SET peak_input_tokens = COALESCE((
                    SELECT SUM(e.input_tokens) FROM usage_events e
                    WHERE e.date = usage_days.date AND {peakPredicate}
                ), 0),
                peak_cached_input_tokens = COALESCE((
                    SELECT SUM(e.cached_input_tokens) FROM usage_events e
                    WHERE e.date = usage_days.date AND {peakPredicate}
                ), 0),
                peak_output_tokens = COALESCE((
                    SELECT SUM(e.output_tokens) FROM usage_events e
                    WHERE e.date = usage_days.date AND {peakPredicate}
                ), 0)
            """);
    }

    private static void EnsureCacheWriteColumns(SqliteConnection connection)
    {
        var dayColumns = ReadTableColumns(connection, "usage_days");
        var eventColumns = ReadTableColumns(connection, "usage_events");
        var addedColumn = false;

        foreach (var column in new[]
                 {
                     "cache_write_input_tokens",
                     "long_context_cache_write_input_tokens",
                     "peak_cache_write_input_tokens"
                 })
        {
            if (dayColumns.Contains(column))
            {
                continue;
            }

            ExecuteNonQuery(connection, $"ALTER TABLE usage_days ADD COLUMN {column} INTEGER NOT NULL DEFAULT 0");
            addedColumn = true;
        }

        if (!eventColumns.Contains("cache_write_input_tokens"))
        {
            ExecuteNonQuery(
                connection,
                "ALTER TABLE usage_events ADD COLUMN cache_write_input_tokens INTEGER NOT NULL DEFAULT 0");
            addedColumn = true;
        }

        if (addedColumn)
        {
            // Existing cache rows were produced before cache-write telemetry
            // was parsed. Mark them incomplete so the normal background scan
            // can rebuild accurate counters from retained source logs.
            ExecuteNonQuery(connection, "UPDATE usage_days SET is_complete = 0");
        }
    }

    private static HashSet<string> ReadTableColumns(SqliteConnection connection, string tableName)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName})";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
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

    private static IReadOnlyList<TokenUsageEvent> ReadDetailEvents(
        SqliteConnection connection,
        DateOnly date,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT event_key, timestamp_local, input_tokens, cached_input_tokens,
                   output_tokens, reasoning_output_tokens, total_tokens, cache_write_input_tokens, model_id, service_tier
            FROM usage_events
            WHERE date = $date
            ORDER BY timestamp_local
            """;
        command.Parameters.AddWithValue("$date", DateKey(date));

        var result = new List<TokenUsageEvent>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(new TokenUsageEvent(
                ParseDateTimeOffset(reader.GetString(1)),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetString(0),
                reader.GetInt64(7), reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9)));
        }

        return UsageEventMerger.Merge(result);
    }

    private static IReadOnlyList<TokenUsageEvent> ReadDetailEvents(
        SqliteConnection connection,
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        CancellationToken cancellationToken = default)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT timestamp_local, input_tokens, cached_input_tokens, output_tokens,
                   reasoning_output_tokens, total_tokens, event_key, cache_write_input_tokens, model_id, service_tier
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
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(new TokenUsageEvent(
                ParseDateTimeOffset(reader.GetString(0)),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetString(6),
                reader.GetInt64(7), reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9)));
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
        summary.Add(usageEvent);

        var date = DateOnly.FromDateTime(usageEvent.Timestamp.DateTime);
        if (!dailyBuckets.TryGetValue(date, out var dailyBucket))
        {
            dailyBucket = new TokenUsageBucket
            {
                StartLocal = new DateTimeOffset(date.Year, date.Month, date.Day, 0, 0, 0, CodexUsageReader.BeijingOffset)
            };
            dailyBuckets[date] = dailyBucket;
        }

        dailyBucket.Add(usageEvent);
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
                bucket.Add(item);
                return bucket;
            })
            .ToList();
    }

    private static TokenUsageBucket CreateBucketFromEvents(
        DateTimeOffset dayStart,
        IEnumerable<TokenUsageEvent> events)
    {
        var bucket = new TokenUsageBucket { StartLocal = dayStart };
        foreach (var item in events)
        {
            bucket.Add(item);
        }

        return bucket;
    }

    private void MarkImportedUsageEventKeys(IEnumerable<TokenUsageEvent> events)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR IGNORE INTO imported_usage_event_keys (event_key) VALUES ($event_key)";
        command.Parameters.AddWithValue("$event_key", "");
        foreach (var key in events
                     .Select(UsageEventMerger.GetStableKey)
                     .Distinct(StringComparer.Ordinal))
        {
            command.Parameters["$event_key"].Value = key;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static void ExecuteNonQuery(SqliteConnection connection, string commandText)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.ExecuteNonQuery();
    }

    private static TokenUsageBucket ReadCachedBucketCounters(
        SqliteDataReader reader,
        int startOrdinal,
        DateTimeOffset startLocal = default)
    {
        return new TokenUsageBucket
        {
            StartLocal = startLocal,
            Events = reader.GetInt64(startOrdinal),
            InputTokens = reader.GetInt64(startOrdinal + 1),
            CachedInputTokens = reader.GetInt64(startOrdinal + 2),
            UncachedInputTokens = reader.GetInt64(startOrdinal + 3),
            OutputTokens = reader.GetInt64(startOrdinal + 4),
            ReasoningOutputTokens = reader.GetInt64(startOrdinal + 5),
            TotalTokens = reader.GetInt64(startOrdinal + 6),
            LongContextEvents = reader.GetInt64(startOrdinal + 7),
            LongContextInputTokens = reader.GetInt64(startOrdinal + 8),
            LongContextCachedInputTokens = reader.GetInt64(startOrdinal + 9),
            LongContextOutputTokens = reader.GetInt64(startOrdinal + 10),
            PeakInputTokens = reader.GetInt64(startOrdinal + 11),
            PeakCachedInputTokens = reader.GetInt64(startOrdinal + 12),
            PeakOutputTokens = reader.GetInt64(startOrdinal + 13),
            CacheWriteInputTokens = reader.GetInt64(startOrdinal + 14),
            LongContextCacheWriteInputTokens = reader.GetInt64(startOrdinal + 15),
            PeakCacheWriteInputTokens = reader.GetInt64(startOrdinal + 16)
        };
    }

    private static TokenUsageBucket ToBucket(CachedDayRecord record)
    {
        return new TokenUsageBucket
        {
            ModelUsage = record.ModelUsage,
            Events = record.Events,
            InputTokens = record.InputTokens,
            CachedInputTokens = record.CachedInputTokens,
            CacheWriteInputTokens = record.CacheWriteInputTokens,
            UncachedInputTokens = record.UncachedInputTokens,
            OutputTokens = record.OutputTokens,
            ReasoningOutputTokens = record.ReasoningOutputTokens,
            TotalTokens = record.TotalTokens,
            LongContextEvents = record.LongContextEvents,
            LongContextInputTokens = record.LongContextInputTokens,
            LongContextCachedInputTokens = record.LongContextCachedInputTokens,
            LongContextCacheWriteInputTokens = record.LongContextCacheWriteInputTokens,
            LongContextOutputTokens = record.LongContextOutputTokens,
            PeakInputTokens = record.PeakInputTokens,
            PeakCachedInputTokens = record.PeakCachedInputTokens,
            PeakCacheWriteInputTokens = record.PeakCacheWriteInputTokens,
            PeakOutputTokens = record.PeakOutputTokens
        };
    }

    private static void CopyBucketCounters(TokenUsageBucket source, CachedDayRecord target)
    {
        target.ModelUsage = source.ModelUsage;
        target.Events = source.Events;
        target.InputTokens = source.InputTokens;
        target.CachedInputTokens = source.CachedInputTokens;
        target.CacheWriteInputTokens = source.CacheWriteInputTokens;
        target.UncachedInputTokens = source.UncachedInputTokens;
        target.OutputTokens = source.OutputTokens;
        target.ReasoningOutputTokens = source.ReasoningOutputTokens;
        target.TotalTokens = source.TotalTokens;
        target.LongContextEvents = source.LongContextEvents;
        target.LongContextInputTokens = source.LongContextInputTokens;
        target.LongContextCachedInputTokens = source.LongContextCachedInputTokens;
        target.LongContextCacheWriteInputTokens = source.LongContextCacheWriteInputTokens;
        target.LongContextOutputTokens = source.LongContextOutputTokens;
        target.PeakInputTokens = source.PeakInputTokens;
        target.PeakCachedInputTokens = source.PeakCachedInputTokens;
        target.PeakCacheWriteInputTokens = source.PeakCacheWriteInputTokens;
        target.PeakOutputTokens = source.PeakOutputTokens;
    }

    private static bool IsNormalizedBucket(TokenUsageBucket bucket)
    {
        var normalized = new TokenUsageBucket
        {
            Events = bucket.Events,
            InputTokens = bucket.InputTokens,
            CachedInputTokens = bucket.CachedInputTokens,
            CacheWriteInputTokens = bucket.CacheWriteInputTokens,
            UncachedInputTokens = bucket.UncachedInputTokens,
            OutputTokens = bucket.OutputTokens,
            ReasoningOutputTokens = bucket.ReasoningOutputTokens,
            TotalTokens = bucket.TotalTokens,
            LongContextEvents = bucket.LongContextEvents,
            LongContextInputTokens = bucket.LongContextInputTokens,
            LongContextCachedInputTokens = bucket.LongContextCachedInputTokens,
            LongContextCacheWriteInputTokens = bucket.LongContextCacheWriteInputTokens,
            LongContextOutputTokens = bucket.LongContextOutputTokens,
            PeakInputTokens = bucket.PeakInputTokens,
            PeakCachedInputTokens = bucket.PeakCachedInputTokens,
            PeakCacheWriteInputTokens = bucket.PeakCacheWriteInputTokens,
            PeakOutputTokens = bucket.PeakOutputTokens
        };
        normalized.NormalizeInPlace();
        return bucket.Events == normalized.Events &&
               bucket.InputTokens == normalized.InputTokens &&
               bucket.CachedInputTokens == normalized.CachedInputTokens &&
               bucket.CacheWriteInputTokens == normalized.CacheWriteInputTokens &&
               bucket.UncachedInputTokens == normalized.UncachedInputTokens &&
               bucket.OutputTokens == normalized.OutputTokens &&
               bucket.ReasoningOutputTokens == normalized.ReasoningOutputTokens &&
               bucket.TotalTokens == normalized.TotalTokens &&
               bucket.LongContextEvents == normalized.LongContextEvents &&
               bucket.LongContextInputTokens == normalized.LongContextInputTokens &&
               bucket.LongContextCachedInputTokens == normalized.LongContextCachedInputTokens &&
               bucket.LongContextCacheWriteInputTokens == normalized.LongContextCacheWriteInputTokens &&
               bucket.LongContextOutputTokens == normalized.LongContextOutputTokens &&
               bucket.PeakInputTokens == normalized.PeakInputTokens &&
               bucket.PeakCachedInputTokens == normalized.PeakCachedInputTokens &&
               bucket.PeakCacheWriteInputTokens == normalized.PeakCacheWriteInputTokens &&
               bucket.PeakOutputTokens == normalized.PeakOutputTokens;
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
        return UsageEventMerger.GetStableKey(item);
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
    // False means persisted snapshot rows existed but none contained a usable
    // normalized quota window. An empty day is valid because some days have no
    // quota observations at all.
    public bool IsValid { get; set; } = true;
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

    private QuotaSnapshotCacheStore(string cachePath)
    {
        this.cachePath = cachePath;
        available = InitializeDatabase();
    }

    public static string GetCachePath(string folderName)
    {
        return UsageCacheStore.GetCachePath(folderName);
    }

    public static IReadOnlyList<DateTimeOffset> GetIncompleteDays(
        string folderName,
        DateTimeOffset startInclusive,
        DateTimeOffset endInclusive,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Load(folderName).ReadIncompleteDays(startInclusive, endInclusive, cancellationToken);
    }

    private IReadOnlyList<DateTimeOffset> ReadIncompleteDays(
        DateTimeOffset startInclusive,
        DateTimeOffset endInclusive,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = new List<DateTimeOffset>();
        var start = StartOfDay(startInclusive);
        var end = StartOfDay(endInclusive);
        if (start > end)
        {
            return result;
        }

        if (!available)
        {
            return EnumerateDaysDescending(start, end, cancellationToken);
        }

        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT date, is_complete
                FROM quota_days
                WHERE date >= $start_date AND date <= $end_date
                """;
            command.Parameters.AddWithValue("$start_date", DateKey(DateOnly.FromDateTime(start.DateTime)));
            command.Parameters.AddWithValue("$end_date", DateKey(DateOnly.FromDateTime(end.DateTime)));

            var completeByDate = new Dictionary<string, bool>(StringComparer.Ordinal);
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    completeByDate[reader.GetString(0)] = reader.GetInt64(1) != 0;
                }
            }

            var snapshotValidityByDate = ReadSnapshotValidityByDate(
                connection,
                DateOnly.FromDateTime(start.DateTime),
                DateOnly.FromDateTime(end.DateTime),
                cancellationToken);

            for (var day = end; day >= start; day = day.AddDays(-1))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var dateKey = DateKey(DateOnly.FromDateTime(day.DateTime));
                var hasInvalidSnapshots = snapshotValidityByDate.TryGetValue(dateKey, out var validity) &&
                                          validity.HasRows &&
                                          !validity.HasUsableWindow;
                if (!completeByDate.TryGetValue(dateKey, out var isComplete) || !isComplete)
                {
                    result.Add(day);
                }
                else if (hasInvalidSnapshots)
                {
                    result.Add(day);
                }
            }

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Preserve the previous fail-safe behavior: an unreadable cache is incomplete.
            return EnumerateDaysDescending(start, end, cancellationToken);
        }
    }

    private static List<DateTimeOffset> EnumerateDaysDescending(
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken = default)
    {
        var result = new List<DateTimeOffset>();
        for (var day = end; day >= start; day = day.AddDays(-1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(day);
        }

        return result;
    }

    private static DateTimeOffset StartOfDay(DateTimeOffset value)
    {
        var local = value.ToOffset(CodexUsageReader.BeijingOffset);
        return new DateTimeOffset(local.Year, local.Month, local.Day, 0, 0, 0, CodexUsageReader.BeijingOffset);
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
        summary.Add(usageEvent);

        var date = DateOnly.FromDateTime(usageEvent.Timestamp.DateTime);
        if (!dailyBuckets.TryGetValue(date, out var dailyBucket))
        {
            dailyBucket = new TokenUsageBucket
            {
                StartLocal = new DateTimeOffset(date.Year, date.Month, date.Day, 0, 0, 0, CodexUsageReader.BeijingOffset)
            };
            dailyBuckets[date] = dailyBucket;
        }

        dailyBucket.Add(usageEvent);
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
                bucket.Add(item);
                return bucket;
            })
            .ToList();
    }

    public static QuotaSnapshotCacheStore Load(string folderName)
    {
        var cachePath = GetCachePath(folderName);
        return Stores.GetOrAdd(
            cachePath,
            static path => new Lazy<QuotaSnapshotCacheStore>(
                () => new QuotaSnapshotCacheStore(path),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    internal static void Forget(string folderName)
    {
        Stores.TryRemove(GetCachePath(folderName), out _);
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

            var normalizedSnapshots = ReadSnapshots(connection, date);
            record.IsValid = normalizedSnapshots.Count == 0 ||
                             normalizedSnapshots.Any(HasUsableQuotaWindow);
            record.Snapshots = normalizedSnapshots
                .Where(HasUsableQuotaWindow)
                .Select(ToCachedQuotaSnapshot)
                .ToList();
            if (!record.IsValid)
            {
                record.IsComplete = false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    public IReadOnlyList<CodexQuotaSnapshot> GetSnapshots(
        DateOnly date,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!available)
        {
            return Array.Empty<CodexQuotaSnapshot>();
        }

        try
        {
            using var connection = OpenConnection();
            return ReadSnapshots(connection, date, cancellationToken)
                .Where(HasUsableQuotaWindow)
                .ToList();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Array.Empty<CodexQuotaSnapshot>();
        }
    }

    public IReadOnlyList<CodexQuotaSnapshot> GetAllSnapshots(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!available)
        {
            return Array.Empty<CodexQuotaSnapshot>();
        }

        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT snapshot_local, limit_id, limit_name,
                       five_hour_used_percent, five_hour_reset_local,
                       week_used_percent, week_reset_local
                FROM quota_snapshots
                ORDER BY snapshot_local
                """;

            var result = new List<CodexQuotaSnapshot>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                result.Add(CodexUsageReader.NormalizeQuotaSnapshotWindows(new CodexQuotaSnapshot(
                    ParseDateTimeOffset(reader.GetString(0)),
                    ReadString(reader, 1),
                    ReadString(reader, 2),
                    ReadDecimal(reader, 3),
                    ReadDateTimeOffset(reader, 4),
                    ReadDecimal(reader, 5),
                    ReadDateTimeOffset(reader, 6))));
            }

            return MergeSnapshotValues(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Array.Empty<CodexQuotaSnapshot>();
        }
    }

    /// <summary>
    /// Streams raw quota snapshots directly from SQLite for transfer/export
    /// paths. Unlike <see cref="GetAllSnapshots"/>, this does not materialize
    /// the complete quota history before the caller starts writing it.
    /// </summary>
    public IEnumerable<CodexQuotaSnapshot> EnumerateSnapshots(
        DateTimeOffset? startInclusive,
        DateTimeOffset? endExclusive,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!available ||
            (startInclusive is null) != (endExclusive is null) ||
            startInclusive >= endExclusive)
        {
            yield break;
        }

        var normalizedStart = startInclusive?.ToOffset(CodexUsageReader.BeijingOffset);
        var normalizedEnd = endExclusive?.ToOffset(CodexUsageReader.BeijingOffset);
        DateTimeOffset? queryStart = normalizedStart is null
            ? null
            : StartOfDay(normalizedStart.Value).AddDays(-1);
        DateTimeOffset? queryEnd = normalizedEnd is null
            ? null
            : StartOfDay(normalizedEnd.Value).AddDays(1);

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = normalizedStart is null
            ? """
                SELECT snapshot_local, limit_id, limit_name,
                       five_hour_used_percent, five_hour_reset_local,
                       week_used_percent, week_reset_local
                FROM quota_snapshots
                ORDER BY snapshot_local
                """
            : """
                SELECT snapshot_local, limit_id, limit_name,
                       five_hour_used_percent, five_hour_reset_local,
                       week_used_percent, week_reset_local
                FROM quota_snapshots
                WHERE date >= $start_date AND date <= $end_date
                ORDER BY snapshot_local
                """;
        if (queryStart is not null && queryEnd is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            command.Parameters.AddWithValue(
                "$start_date",
                DateKey(DateOnly.FromDateTime(queryStart.Value.DateTime)));
            command.Parameters.AddWithValue(
                "$end_date",
                DateKey(DateOnly.FromDateTime(queryEnd.Value.DateTime)));
        }

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = CodexUsageReader.NormalizeQuotaSnapshotWindows(new CodexQuotaSnapshot(
                ParseDateTimeOffset(reader.GetString(0)),
                ReadString(reader, 1),
                ReadString(reader, 2),
                ReadDecimal(reader, 3),
                ReadDateTimeOffset(reader, 4),
                ReadDecimal(reader, 5),
                ReadDateTimeOffset(reader, 6)));
            if (!HasUsableQuotaWindow(snapshot))
            {
                continue;
            }
            if (normalizedStart is not null)
            {
                var snapshotLocal = snapshot.SnapshotLocal.ToOffset(CodexUsageReader.BeijingOffset);
                if (snapshotLocal < normalizedStart.Value || snapshotLocal >= normalizedEnd!.Value)
                {
                    continue;
                }

                snapshot = snapshot with { SnapshotLocal = snapshotLocal };
            }

            yield return snapshot;
        }
    }

    public int MergeImportedSnapshots(IReadOnlyList<CodexQuotaSnapshot> importedSnapshots)
    {
        if (!available || importedSnapshots.Count == 0)
        {
            return 0;
        }

        var normalizedImports = importedSnapshots
            .Select(item => CodexUsageReader.NormalizeQuotaSnapshotWindows(item with
            {
                SnapshotLocal = item.SnapshotLocal.ToOffset(CodexUsageReader.BeijingOffset),
                FiveHourResetAtLocal = item.FiveHourResetAtLocal?.ToOffset(CodexUsageReader.BeijingOffset),
                WeekResetAtLocal = item.WeekResetAtLocal?.ToOffset(CodexUsageReader.BeijingOffset)
            }))
            .Where(HasUsableQuotaWindow)
            .ToList();
        if (normalizedImports.Count == 0)
        {
            return 0;
        }
        MarkImportedSnapshotKeys(normalizedImports);

        var added = 0;
        foreach (var group in normalizedImports
                     .GroupBy(item => DateOnly.FromDateTime(item.SnapshotLocal.DateTime))
                     .OrderBy(item => item.Key))
        {
            var date = group.Key;
            var existing = GetSnapshots(date);
            var existingKeys = existing
                .Select(BuildSnapshotKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var merged = MergeSnapshotValues(existing.Concat(group));
            added += merged.Count(item => !existingKeys.Contains(BuildSnapshotKey(item)));

            var hasRecord = TryGetRecord(date, out var record);
            Put(
                date,
                merged,
                hasRecord && record.IsComplete,
                hasRecord ? record.ScannedThroughLocal : null,
                propagateErrors: true);
        }

        return added;
    }

    private static IReadOnlyList<CodexQuotaSnapshot> MergeSnapshotValues(
        IEnumerable<CodexQuotaSnapshot> snapshots)
    {
        return snapshots
            .Select(CodexUsageReader.NormalizeQuotaSnapshotWindows)
            .Where(HasUsableQuotaWindow)
            .GroupBy(BuildSnapshotKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderBy(item => item.IsAnomaly)
                .ThenByDescending(item => SnapshotCompleteness(item))
                .First())
            .OrderBy(item => item.SnapshotLocal)
            .ToList();
    }

    private static int SnapshotCompleteness(CodexQuotaSnapshot snapshot)
    {
        return (snapshot.FiveHourUsedPercent is null ? 0 : 1) +
               (snapshot.FiveHourResetAtLocal is null ? 0 : 1) +
               (snapshot.WeekUsedPercent is null ? 0 : 1) +
               (snapshot.WeekResetAtLocal is null ? 0 : 1);
    }

    public IReadOnlyList<CodexQuotaSnapshot> GetSnapshots(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        startLocal = startLocal.ToOffset(CodexUsageReader.BeijingOffset);
        endLocal = endLocal.ToOffset(CodexUsageReader.BeijingOffset);
        if (!available || startLocal >= endLocal)
        {
            return Array.Empty<CodexQuotaSnapshot>();
        }

        try
        {
            // Query a one-day buffer on both sides so older rows persisted with
            // a non-Beijing offset are still found; filter by the normalized
            // instant below.
            var queryStart = StartOfDay(startLocal).AddDays(-1);
            var queryEnd = StartOfDay(endLocal).AddDays(1);
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT snapshot_local, limit_id, limit_name,
                       five_hour_used_percent, five_hour_reset_local,
                       week_used_percent, week_reset_local
                FROM quota_snapshots
                WHERE date >= $start_date AND date <= $end_date
                ORDER BY snapshot_local
                """;
            command.Parameters.AddWithValue("$start_date", DateKey(DateOnly.FromDateTime(queryStart.DateTime)));
            command.Parameters.AddWithValue("$end_date", DateKey(DateOnly.FromDateTime(queryEnd.DateTime)));

            var result = new List<CodexQuotaSnapshot>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var snapshot = CodexUsageReader.NormalizeQuotaSnapshotWindows(new CodexQuotaSnapshot(
                    ParseDateTimeOffset(reader.GetString(0)),
                    ReadString(reader, 1),
                    ReadString(reader, 2),
                    ReadDecimal(reader, 3),
                    ReadDateTimeOffset(reader, 4),
                    ReadDecimal(reader, 5),
                    ReadDateTimeOffset(reader, 6)));
                var snapshotLocal = snapshot.SnapshotLocal.ToOffset(CodexUsageReader.BeijingOffset);
                if (snapshotLocal >= startLocal && snapshotLocal < endLocal)
                {
                    result.Add(snapshot with { SnapshotLocal = snapshotLocal });
                }
            }

            return MergeSnapshotValues(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Array.Empty<CodexQuotaSnapshot>();
        }
    }

    public IReadOnlyList<CodexQuotaSnapshot> GetTimelineSnapshots(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        startLocal = startLocal.ToOffset(CodexUsageReader.BeijingOffset);
        endLocal = endLocal.ToOffset(CodexUsageReader.BeijingOffset);
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
                cancellationToken.ThrowIfCancellationRequested();
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Array.Empty<CodexQuotaSnapshot>();
        }
    }

    public void PutTimelineSnapshots(
        IReadOnlyList<CodexQuotaSnapshot> snapshots,
        IReadOnlyDictionary<DateTimeOffset, (DateTimeOffset? Before, DateTimeOffset? After)> sources,
        CancellationToken cancellationToken = default)
    {
        if (!available || snapshots.Count == 0)
        {
            return;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
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
            command.Parameters.AddWithValue("$date", "");
            command.Parameters.AddWithValue("$anchor_local", "");
            command.Parameters.AddWithValue("$limit_id", DBNull.Value);
            command.Parameters.AddWithValue("$limit_name", DBNull.Value);
            command.Parameters.AddWithValue("$five_hour_used_percent", DBNull.Value);
            command.Parameters.AddWithValue("$five_hour_reset_local", DBNull.Value);
            command.Parameters.AddWithValue("$week_used_percent", DBNull.Value);
            command.Parameters.AddWithValue("$week_reset_local", DBNull.Value);
            command.Parameters.AddWithValue("$is_interpolated", 0);
            command.Parameters.AddWithValue("$before_snapshot_local", DBNull.Value);
            command.Parameters.AddWithValue("$after_snapshot_local", DBNull.Value);
            command.Parameters.AddWithValue("$updated_local", "");
            foreach (var snapshot in snapshots.OrderBy(item => item.SnapshotLocal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                sources.TryGetValue(snapshot.SnapshotLocal, out var source);
                command.Parameters["$date"].Value = DateKey(DateOnly.FromDateTime(snapshot.SnapshotLocal.DateTime));
                command.Parameters["$anchor_local"].Value = FormatDateTimeOffset(snapshot.SnapshotLocal);
                command.Parameters["$limit_id"].Value = ToDbValue(snapshot.LimitId);
                command.Parameters["$limit_name"].Value = ToDbValue(snapshot.LimitName);
                command.Parameters["$five_hour_used_percent"].Value = ToDbValue(snapshot.FiveHourUsedPercent);
                command.Parameters["$five_hour_reset_local"].Value = ToDbValue(snapshot.FiveHourResetAtLocal);
                command.Parameters["$week_used_percent"].Value = ToDbValue(snapshot.WeekUsedPercent);
                command.Parameters["$week_reset_local"].Value = ToDbValue(snapshot.WeekResetAtLocal);
                command.Parameters["$is_interpolated"].Value = source.Before != snapshot.SnapshotLocal || source.After != snapshot.SnapshotLocal ? 1 : 0;
                command.Parameters["$before_snapshot_local"].Value = ToDbValue(source.Before);
                command.Parameters["$after_snapshot_local"].Value = ToDbValue(source.After);
                command.Parameters["$updated_local"].Value = FormatDateTimeOffset(DateTimeOffset.UtcNow.ToOffset(CodexUsageReader.BeijingOffset));
                command.ExecuteNonQuery();
            }

            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Timeline materialization is optional; raw snapshots remain available as a fallback.
        }
    }

    public IReadOnlyList<DateTimeOffset> GetIncompleteTimelineDays(
        DateTimeOffset startInclusive,
        DateTimeOffset endInclusive,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        startInclusive = startInclusive.ToOffset(CodexUsageReader.BeijingOffset);
        endInclusive = endInclusive.ToOffset(CodexUsageReader.BeijingOffset);
        if (!available)
        {
            return EnumerateDaysDescending(
                StartOfDay(startInclusive),
                StartOfDay(endInclusive),
                cancellationToken);
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
                cancellationToken.ThrowIfCancellationRequested();
                var date = DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture);
                result.Add(new DateTimeOffset(date.Year, date.Month, date.Day, 0, 0, 0, CodexUsageReader.BeijingOffset));
            }

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // A failed cache read must remain pending; otherwise the background
            // warmer could report a timeline day as complete without persisting
            // any materialized anchors.
            return EnumerateDaysDescending(
                StartOfDay(startInclusive),
                StartOfDay(endInclusive),
                cancellationToken);
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
                deleteSnapshotsCommand.CommandText = """
                    DELETE FROM quota_snapshots
                    WHERE date = $date
                      AND snapshot_key NOT IN (SELECT snapshot_key FROM imported_quota_snapshot_keys)
                    """;
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
        DateTimeOffset? scannedThroughLocal,
        CancellationToken cancellationToken = default,
        bool propagateErrors = false)
    {
        if (!available)
        {
            return;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalizedSnapshots = snapshots
                .Select(CodexUsageReader.NormalizeQuotaSnapshotWindows)
                .ToList();
            var hasInputSnapshots = normalizedSnapshots.Count > 0;
            normalizedSnapshots = normalizedSnapshots
                .Where(HasUsableQuotaWindow)
                .ToList();
            var effectiveComplete = isComplete &&
                                    (!hasInputSnapshots ||
                                     normalizedSnapshots.Any(HasUsableQuotaWindow));
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
                command.Parameters.AddWithValue("$is_complete", effectiveComplete ? 1 : 0);
                command.Parameters.AddWithValue("$scanned_through_local", ToDbValue(scannedThroughLocal));
                command.ExecuteNonQuery();
            }

            using (var deleteCommand = connection.CreateCommand())
            {
                deleteCommand.Transaction = transaction;
                deleteCommand.CommandText = """
                    DELETE FROM quota_snapshots
                    WHERE date = $date
                      AND snapshot_key NOT IN (SELECT snapshot_key FROM imported_quota_snapshot_keys)
                    """;
                deleteCommand.Parameters.AddWithValue("$date", key);
                deleteCommand.ExecuteNonQuery();
            }

            using (var insertCommand = connection.CreateCommand())
            {
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
                insertCommand.Parameters.AddWithValue("$snapshot_key", "");
                insertCommand.Parameters.AddWithValue("$snapshot_local", "");
                insertCommand.Parameters.AddWithValue("$limit_id", DBNull.Value);
                insertCommand.Parameters.AddWithValue("$limit_name", DBNull.Value);
                insertCommand.Parameters.AddWithValue("$five_hour_used_percent", DBNull.Value);
                insertCommand.Parameters.AddWithValue("$five_hour_reset_local", DBNull.Value);
                insertCommand.Parameters.AddWithValue("$week_used_percent", DBNull.Value);
                insertCommand.Parameters.AddWithValue("$week_reset_local", DBNull.Value);
                foreach (var snapshot in normalizedSnapshots.OrderBy(item => item.SnapshotLocal))
                {
                    insertCommand.Parameters["$snapshot_key"].Value = BuildSnapshotKey(snapshot);
                    insertCommand.Parameters["$snapshot_local"].Value = FormatDateTimeOffset(snapshot.SnapshotLocal);
                    insertCommand.Parameters["$limit_id"].Value = ToDbValue(snapshot.LimitId);
                    insertCommand.Parameters["$limit_name"].Value = ToDbValue(snapshot.LimitName);
                    insertCommand.Parameters["$five_hour_used_percent"].Value = ToDbValue(snapshot.FiveHourUsedPercent);
                    insertCommand.Parameters["$five_hour_reset_local"].Value = ToDbValue(snapshot.FiveHourResetAtLocal);
                    insertCommand.Parameters["$week_used_percent"].Value = ToDbValue(snapshot.WeekUsedPercent);
                    insertCommand.Parameters["$week_reset_local"].Value = ToDbValue(snapshot.WeekResetAtLocal);
                    insertCommand.ExecuteNonQuery();
                }
            }

            var dayStart = new DateTimeOffset(date.Year, date.Month, date.Day, 0, 0, 0, CodexUsageReader.BeijingOffset);
            DeleteTimelineRange(connection, transaction, dayStart.AddDays(-1), dayStart.AddDays(2));

            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception) when (!propagateErrors)
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
                CREATE TABLE IF NOT EXISTS imported_quota_snapshot_keys (
                    snapshot_key TEXT PRIMARY KEY
                )
                """);
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

    private static bool HasUsableQuotaWindow(CachedQuotaSnapshot snapshot)
    {
        return snapshot.FiveHourUsedPercent is not null ||
               snapshot.WeekUsedPercent is not null;
    }

    private static bool HasUsableQuotaWindow(CodexQuotaSnapshot snapshot)
    {
        return snapshot.FiveHourUsedPercent is not null ||
               snapshot.WeekUsedPercent is not null;
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

    private static IReadOnlyList<CodexQuotaSnapshot> ReadSnapshots(
        SqliteConnection connection,
        DateOnly date,
        CancellationToken cancellationToken = default)
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
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(CodexUsageReader.NormalizeQuotaSnapshotWindows(new CodexQuotaSnapshot(
                ParseDateTimeOffset(reader.GetString(0)),
                ReadString(reader, 1),
                ReadString(reader, 2),
                ReadDecimal(reader, 3),
                ReadDateTimeOffset(reader, 4),
                ReadDecimal(reader, 5),
                ReadDateTimeOffset(reader, 6))));
        }

        return result;
    }

    private static Dictionary<string, (bool HasRows, bool HasUsableWindow)> ReadSnapshotValidityByDate(
        SqliteConnection connection,
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken = default)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT date, snapshot_local, limit_id, limit_name,
                   five_hour_used_percent, five_hour_reset_local,
                   week_used_percent, week_reset_local
            FROM quota_snapshots
            WHERE date >= $start_date AND date <= $end_date
            ORDER BY snapshot_local
            """;
        command.Parameters.AddWithValue("$start_date", DateKey(startDate));
        command.Parameters.AddWithValue("$end_date", DateKey(endDate));

        var result = new Dictionary<string, (bool HasRows, bool HasUsableWindow)>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dateKey = reader.GetString(0);
            var snapshot = CodexUsageReader.NormalizeQuotaSnapshotWindows(new CodexQuotaSnapshot(
                ParseDateTimeOffset(reader.GetString(1)),
                ReadString(reader, 2),
                ReadString(reader, 3),
                ReadDecimal(reader, 4),
                ReadDateTimeOffset(reader, 5),
                ReadDecimal(reader, 6),
                ReadDateTimeOffset(reader, 7)));
            var hasUsableWindow = HasUsableQuotaWindow(snapshot);
            result[dateKey] = result.TryGetValue(dateKey, out var existing)
                ? (true, existing.HasUsableWindow || hasUsableWindow)
                : (true, hasUsableWindow);
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

    private void MarkImportedSnapshotKeys(IEnumerable<CodexQuotaSnapshot> snapshots)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR IGNORE INTO imported_quota_snapshot_keys (snapshot_key) VALUES ($snapshot_key)";
        command.Parameters.AddWithValue("$snapshot_key", "");
        foreach (var key in snapshots
                     .Select(BuildSnapshotKey)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            command.Parameters["$snapshot_key"].Value = key;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
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
    private readonly CodexModelContext modelContext = new();
    public string? ModelId => modelContext.ModelId;
    public string? ServiceTier => modelContext.ServiceTier;
    private const string CollaborationBootstrap =
        "You are an agent in a team of agents collaborating to complete a task.";

    private bool metadataChecked;
    private bool isSubagent;
    private bool waitingForLiveTaskStart;
    private bool liveTrafficStarted = true;
    private DateTimeOffset? lastReplayTimestamp;

    public bool ShouldReadTokenCount(string line)
    {
        modelContext.Observe(line);
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

    internal static DateTimeOffset? TryReadRecordTimestamp(string line)
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

    /// <summary>Test seam: overrides the Codex home instead of the user profile.</summary>
    internal static string? OverrideCodexHome { get; set; }

    private static readonly LiveFileTailReader UsageTailReader = new();
    private static readonly LiveFileTailReader QuotaTailReader = new();
    private static readonly ConcurrentDictionary<string, SubagentReplayFilter> UsageReplayFilters =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, SubagentReplayFilter> QuotaReplayFilters =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly object QuotaHistoryCacheSync = new();
    private static readonly List<RateLimitSnapshot> QuotaHistorySnapshotCache = new();
    private static readonly HashSet<QuotaHistoryKey> QuotaHistoryKeyCache = new(QuotaHistoryKeyComparer.Instance);
    private static string? quotaHistoryCachedPath;
    private static long quotaHistoryCachedLength = -1;
    private static DateTime quotaHistoryCachedWriteTimeUtc;

    private readonly record struct RateLimitWindowSnapshot(
        decimal UsedPercent,
        int WindowMinutes,
        DateTimeOffset? ResetAtLocal);

    private readonly record struct RateLimitSnapshot(
        DateTimeOffset TimestampLocal,
        string? LimitId,
        string? LimitName,
        RateLimitWindowSnapshot? FiveHour,
        RateLimitWindowSnapshot? Week,
        long ModelContextWindow);

    private readonly record struct RateLimitScanResult(
        IReadOnlyList<RateLimitSnapshot> Snapshots,
        bool IsComplete);

    private readonly record struct QuotaSnapshotScanResult(
        IReadOnlyList<CodexQuotaSnapshot> Snapshots,
        bool IsComplete);

    private sealed record QuotaHistoryKey(DateTimeOffset SnapshotLocal, string LimitId);

    private sealed class QuotaHistoryKeyComparer : IEqualityComparer<QuotaHistoryKey>
    {
        public static QuotaHistoryKeyComparer Instance { get; } = new();

        public bool Equals(QuotaHistoryKey? first, QuotaHistoryKey? second)
        {
            return ReferenceEquals(first, second) ||
                   first is not null &&
                   second is not null &&
                   first.SnapshotLocal == second.SnapshotLocal &&
                   string.Equals(first.LimitId, second.LimitId, StringComparison.OrdinalIgnoreCase);
        }

        public int GetHashCode(QuotaHistoryKey value)
        {
            return HashCode.Combine(
                value.SnapshotLocal,
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.LimitId));
        }
    }

    private sealed record MaterializedQuotaPoint(
        CodexQuotaSnapshot Snapshot,
        DateTimeOffset? BeforeSnapshotLocal,
        DateTimeOffset? AfterSnapshotLocal);

    public static bool ClearCache()
    {
        ResetLiveFileCursors();
        CodexQuotaCycleReader.InvalidateCache();
        var deleted = UsageCacheStore.Delete(CacheFolder);
        lock (QuotaHistoryCacheSync)
        {
            ResetQuotaHistoryCache(GetQuotaHistoryPath());
        }

        return deleted;
    }

    public static bool ClearCachedDay(DateOnly date)
    {
        ResetLiveFileCursors();
        CodexQuotaCycleReader.InvalidateCache();
        var usageDeleted = UsageCacheStore.DeleteDay(CacheFolder, date);
        var quotaDeleted = QuotaSnapshotCacheStore.DeleteDay(CacheFolder, date);
        return usageDeleted || quotaDeleted;
    }

    public static IReadOnlyList<DateTimeOffset> GetIncompleteHistoricalDays(
        DateTimeOffset startInclusive,
        DateTimeOffset endInclusive,
        CancellationToken cancellationToken = default)
    {
        return UsageCacheStore.GetIncompleteDays(
            CacheFolder,
            startInclusive,
            endInclusive,
            cancellationToken);
    }

    public static TokenUsageSummary ReadCachedRange(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        CancellationToken cancellationToken = default)
    {
        return UsageCacheStore.Load(CacheFolder).ReadRange(startLocal, endLocal, cancellationToken);
    }

    public static IReadOnlyList<TokenUsageBucket> ReadCachedDetailRows(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        CancellationToken cancellationToken = default)
    {
        return UsageCacheStore.Load(CacheFolder).ReadDetailRows(startLocal, endLocal, cancellationToken);
    }

    public static CodexQuotaEstimate? ReadQuotaEstimate(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow.ToOffset(BeijingOffset);
        var directSnapshot = CodexAppServerQuotaReader.ReadCurrent(cancellationToken);
        if (directSnapshot is not null)
        {
            directSnapshot = NormalizeQuotaSnapshotWindows(directSnapshot);
            if (IsGeneralCodexQuotaSnapshot(directSnapshot))
            {
                AppendQuotaHistoryIfNew(ToRateLimitSnapshot(directSnapshot));
                return BuildQuotaEstimate(directSnapshot, now, cancellationToken: cancellationToken);
            }
        }

        // Older CLI builds do not expose account/rateLimits/read. Keep the
        // session-log path as a compatibility fallback in that case.
        var liveEnd = now.AddMinutes(5);
        var recentStart = now.AddMinutes(-30);
        var snapshot = ReadQuotaSnapshotsUncached(recentStart, liveEnd, cancellationToken).Snapshots
            .Where(IsGeneralCodexQuotaSnapshot)
            .OrderByDescending(item => item.SnapshotLocal)
            .FirstOrDefault()
            ?? ReadQuotaSnapshotsCached(recentStart, liveEnd, cancellationToken)
            .Where(IsGeneralCodexQuotaSnapshot)
            .OrderByDescending(item => item.SnapshotLocal)
            .FirstOrDefault()
            ?? ReadQuotaSnapshotsCached(StartOfDay(now), liveEnd, cancellationToken)
            .Where(IsGeneralCodexQuotaSnapshot)
            .OrderByDescending(item => item.SnapshotLocal)
            .FirstOrDefault()
            ?? ReadCachedAndHistoricalQuotaSnapshots(now.AddDays(-8), liveEnd, cancellationToken)
            .Where(IsGeneralCodexQuotaSnapshot)
            .OrderByDescending(item => item.SnapshotLocal)
            .FirstOrDefault();
        if (snapshot is null || !QuotaFreshness.IsFresh(snapshot.SnapshotLocal, now))
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return BuildQuotaEstimate(snapshot, now, cancellationToken: cancellationToken);
    }

    public static CodexQuotaEstimate? ReadCachedQuotaEstimate(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow.ToOffset(BeijingOffset);
        var snapshot = ReadCachedAndHistoricalQuotaSnapshots(
                now.AddDays(-8),
                now.AddMinutes(5),
                cancellationToken)
            .Where(IsGeneralCodexQuotaSnapshot)
            .OrderByDescending(item => item.SnapshotLocal)
            .FirstOrDefault();
        if (snapshot is null || !QuotaFreshness.IsFresh(snapshot.SnapshotLocal, now))
        {
            return null;
        }

        return BuildQuotaEstimate(snapshot, now, includeLiveToday: false, cancellationToken);
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
        DateTimeOffset endLocal,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return QuotaSnapshotCacheStore.Load(CacheFolder)
            .GetSnapshots(startLocal, endLocal, cancellationToken)
            .Select(NormalizeQuotaSnapshotWindows)
            .ToList();
    }

    public static IReadOnlyList<CodexQuotaSnapshot> ReadMaterializedQuotaTimeline(
        IEnumerable<DateTimeOffset> anchors,
        IEnumerable<CodexQuotaSnapshot>? supplementalSnapshots = null,
        bool refreshExisting = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
        var cached = cache.GetTimelineSnapshots(
                normalizedAnchors[0],
                normalizedAnchors[^1].AddTicks(1),
                cancellationToken)
            .Select(NormalizeQuotaSnapshotWindows)
            .ToList();
        var cachedByAnchor = cached.ToDictionary(item => item.SnapshotLocal, item => item);
        var missingAnchors = refreshExisting
            ? normalizedAnchors
            : normalizedAnchors.Where(anchor => !cachedByAnchor.ContainsKey(anchor)).ToList();

        if (missingAnchors.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceStart = StartOfDay(missingAnchors[0]).AddDays(-1);
            var sourceEnd = StartOfDay(missingAnchors[^1]).AddDays(2);
            var sourceSnapshots = PrepareQuotaTimelineSources(
                ReadCachedAndHistoricalQuotaSnapshots(sourceStart, sourceEnd, cancellationToken)
                    .Concat(supplementalSnapshots ?? Array.Empty<CodexQuotaSnapshot>()),
                cancellationToken);
            var allIndex = new QuotaTimelineSnapshotIndex(sourceSnapshots);
            var fiveHourIndex = new QuotaTimelineSnapshotIndex(sourceSnapshots.Where(item => item.FiveHourUsedPercent is not null));
            var weekIndex = new QuotaTimelineSnapshotIndex(sourceSnapshots.Where(item => item.WeekUsedPercent is not null));
            var materialized = missingAnchors
                .Select(anchor =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return MaterializeQuotaPoint(anchor, allIndex, fiveHourIndex, weekIndex);
                })
                .ToList();
            var sourceMap = materialized.ToDictionary(
                item => item.Snapshot.SnapshotLocal,
                item => (item.BeforeSnapshotLocal, item.AfterSnapshotLocal));
            cache.PutTimelineSnapshots(
                materialized.Select(item => item.Snapshot).ToList(),
                sourceMap,
                cancellationToken);

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
        DateTimeOffset endInclusive,
        CancellationToken cancellationToken = default)
    {
        return QuotaSnapshotCacheStore.Load(CacheFolder)
            .GetIncompleteTimelineDays(startInclusive, endInclusive, cancellationToken);
    }

    public static void WarmQuotaTimelineDay(
        DateTimeOffset dayLocal,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dayStart = StartOfDay(dayLocal);
        var dayEnd = dayStart.AddDays(1);
        var rows = ReadCachedDetailRows(dayStart, dayEnd, cancellationToken);
        if (rows.Count == 0)
        {
            return;
        }

        _ = ReadMaterializedQuotaTimeline(
            rows.Select(row => row.StartLocal),
            cancellationToken: cancellationToken);
    }

    public static IReadOnlyList<CodexQuotaSnapshot> ReadCachedAndHistoricalQuotaSnapshots(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cached = ReadCachedQuotaSnapshots(startLocal, endLocal, cancellationToken);
        var history = ReadQuotaHistoryQuotaSnapshots(startLocal, endLocal, cancellationToken);
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
        IEnumerable<CodexQuotaSnapshot> snapshots,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
            var usefulIndex = new QuotaTimelineSnapshotIndex(useful);
            filtered = filtered
                .Where(snapshot =>
                    !IsZeroTimelineQuotaSnapshot(snapshot) ||
                    !usefulIndex.AnyNearby(snapshot.SnapshotLocal, TimeSpan.FromMinutes(10),
                        other => TimelineQuotaWindowsOverlap(snapshot, other)))
                .ToList();
        }

        cancellationToken.ThrowIfCancellationRequested();
        return CodexQuotaCycleReader.MarkTransientResetOutliers(filtered, cancellationToken)
            .Where(item => !item.IsAnomaly)
            .OrderBy(item => item.SnapshotLocal)
            .ToList();
    }

    private static MaterializedQuotaPoint MaterializeQuotaPoint(
        DateTimeOffset anchor,
        QuotaTimelineSnapshotIndex allIndex,
        QuotaTimelineSnapshotIndex fiveHourIndex,
        QuotaTimelineSnapshotIndex weekIndex)
    {
        var (before, after, nearest) = allIndex.Find(anchor);
        if (nearest is not null && Math.Abs((nearest.SnapshotLocal - anchor).TotalMinutes) <= 2)
        {
            return new MaterializedQuotaPoint(
                nearest with { SnapshotLocal = anchor, IsAnomaly = false },
                nearest.SnapshotLocal,
                nearest.SnapshotLocal);
        }

        var fiveHour = InterpolateTimelineWindow(
            anchor,
            fiveHourIndex,
            item => item.FiveHourUsedPercent,
            item => item.FiveHourResetAtLocal);
        var week = InterpolateTimelineWindow(
            anchor,
            weekIndex,
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
        QuotaTimelineSnapshotIndex index,
        Func<CodexQuotaSnapshot, decimal?> usedSelector,
        Func<CodexQuotaSnapshot, DateTimeOffset?> resetSelector)
    {
        var (before, after, nearest) = index.Find(anchor);
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
        DateTimeOffset endLocal,
        CancellationToken cancellationToken = default)
    {
        var historySnapshots = ReadQuotaHistorySnapshots(startLocal, endLocal, cancellationToken)
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
        DateTimeOffset endInclusive,
        CancellationToken cancellationToken = default)
    {
        return QuotaSnapshotCacheStore.GetIncompleteDays(
            CacheFolder,
            startInclusive,
            endInclusive,
            cancellationToken);
    }

    public static void WarmQuotaSnapshotDay(
        DateTimeOffset dayLocal,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dayStart = StartOfDay(dayLocal);
        var dayEnd = dayStart.AddDays(1);
        var now = DateTimeOffset.UtcNow.ToOffset(BeijingOffset);
        if (dayStart <= now && dayEnd > now)
        {
            dayEnd = now;
        }

        _ = ReadQuotaSnapshotsCached(dayStart, dayEnd, cancellationToken);
        WarmQuotaTimelineDay(dayStart.AddDays(-1), cancellationToken);
        WarmQuotaTimelineDay(dayStart, cancellationToken);
        WarmQuotaTimelineDay(dayStart.AddDays(1), cancellationToken);
    }

    public static void WarmQuotaSnapshotDays(
        IEnumerable<DateTimeOffset> daysLocal,
        CancellationToken cancellationToken = default,
        Action<DateTimeOffset>? dayCompleted = null,
        Action<int, int>? fileProgress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var today = StartOfDay(BeijingClock.Now);
        var days = daysLocal
            .Select(StartOfDay)
            .Where(day => day < today)
            .Distinct()
            .OrderBy(item => item)
            .ToList();
        if (days.Count == 0)
        {
            return;
        }

        var incomplete = GetIncompleteQuotaSnapshotDays(days[0], days[^1], cancellationToken).ToHashSet();
        foreach (var day in days.Where(day => !incomplete.Contains(day)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            dayCompleted?.Invoke(day);
        }
        days = days.Where(incomplete.Contains).ToList();
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
        var liveScan = ReadRateLimitSnapshots(startLocal, endLocal, cancellationToken, fileProgress);
        var liveSnapshots = liveScan.Snapshots;
        var liveQuotaSnapshots = liveSnapshots
            .Where(IsQuotaHistorySnapshot)
            .Select(ToCodexQuotaSnapshot)
            .ToList();
        var liveSparkTimes = liveQuotaSnapshots
            .Where(IsGpt53QuotaSnapshot)
            .Select(item => item.SnapshotLocal)
            .ToHashSet();
        var historyQuotaSnapshots = ReadQuotaHistorySnapshots(startLocal, endLocal, cancellationToken)
            .Where(IsQuotaHistorySnapshot)
            .Where(item => !IsStaleGeneralSnapshotForLiveSpark(item, liveSparkTimes))
            .Select(ToCodexQuotaSnapshot);
        var scannedByDate = MergeQuotaSnapshots(liveQuotaSnapshots.Concat(historyQuotaSnapshots))
            .Where(item => requestedDates.Contains(DateOnly.FromDateTime(item.SnapshotLocal.DateTime)))
            .GroupBy(item => DateOnly.FromDateTime(item.SnapshotLocal.DateTime))
            .ToDictionary(group => group.Key, group => (IReadOnlyList<CodexQuotaSnapshot>)group.ToList());

        var cache = QuotaSnapshotCacheStore.Load(CacheFolder);
        foreach (var day in days.OrderByDescending(day => day))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var date = DateOnly.FromDateTime(day.DateTime);
            var existing = cache.GetSnapshots(date, cancellationToken);
            scannedByDate.TryGetValue(date, out var scanned);
            var merged = MergeQuotaSnapshots(existing.Concat(scanned ?? Array.Empty<CodexQuotaSnapshot>())).ToList();
            cache.Put(
                date,
                merged,
                isComplete: liveScan.IsComplete,
                scannedThroughLocal: day.AddDays(1).AddTicks(-1),
                cancellationToken: cancellationToken,
                propagateErrors: true);
            if (liveScan.IsComplete && cache.TryGetRecord(date, out var record) && record.IsComplete)
            {
                dayCompleted?.Invoke(day);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        cache.Save();
        CodexQuotaCycleReader.InvalidateCache();
    }

    private static IReadOnlyList<CodexQuotaSnapshot> ReadQuotaSnapshotsCached(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        CancellationToken cancellationToken = default)
    {
        var result = new List<CodexQuotaSnapshot>();
        var cache = QuotaSnapshotCacheStore.Load(CacheFolder);
        var cacheChanged = false;
        var now = DateTimeOffset.UtcNow.ToOffset(BeijingOffset);
        var todayStart = StartOfDay(now);

        for (var dayStart = StartOfDay(startLocal); dayStart < endLocal; dayStart = dayStart.AddDays(1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dayEnd = dayStart.AddDays(1);
            var clippedStart = Max(dayStart, startLocal);
            var clippedEnd = Min(dayEnd, endLocal);
            if (clippedStart >= clippedEnd)
            {
                continue;
            }

            var date = DateOnly.FromDateTime(dayStart.DateTime);
            var daySnapshots = cache.GetSnapshots(date, cancellationToken).ToList();
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
                (fullHistoricalDay && record.IsComplete && record.IsValid ||
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
                    var scannedResult = ReadQuotaSnapshotsUncached(scanStart, scanEnd, cancellationToken);
                    daySnapshots = MergeQuotaSnapshots(daySnapshots
                        .Concat(scannedResult.Snapshots)
                        .Where(item => item.SnapshotLocal >= dayStart && item.SnapshotLocal < dayEnd))
                        .ToList();
                    cache.Put(
                        date,
                        daySnapshots,
                        isComplete: fullHistoricalDay && scannedResult.IsComplete,
                        scannedThroughLocal: scanEnd.AddTicks(-1),
                        cancellationToken: cancellationToken);
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

    private static QuotaSnapshotScanResult ReadQuotaSnapshotsUncached(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var liveScan = ReadRateLimitSnapshots(startLocal, endLocal, cancellationToken);
        foreach (var item in liveScan.Snapshots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AppendQuotaHistoryIfNew(item);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var liveQuotaSnapshots = liveScan.Snapshots
            .Where(IsQuotaHistorySnapshot)
            .Select(ToCodexQuotaSnapshot)
            .ToList();
        var liveSparkTimes = liveQuotaSnapshots
            .Where(IsGpt53QuotaSnapshot)
            .Select(item => item.SnapshotLocal)
            .ToHashSet();
        var historyQuotaSnapshots = ReadQuotaHistorySnapshots(startLocal, endLocal, cancellationToken)
            .Where(IsQuotaHistorySnapshot)
            .Where(item => !IsStaleGeneralSnapshotForLiveSpark(item, liveSparkTimes))
            .Select(ToCodexQuotaSnapshot);

        return new QuotaSnapshotScanResult(
            MergeQuotaSnapshots(liveQuotaSnapshots.Concat(historyQuotaSnapshots)).ToList(),
            liveScan.IsComplete);
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
        var normalizedFiveHour = QuotaPercentRules.Normalize(snapshot.FiveHourUsedPercent);
        var normalizedWeek = QuotaPercentRules.Normalize(snapshot.WeekUsedPercent);
        if (normalizedFiveHour != snapshot.FiveHourUsedPercent ||
            normalizedWeek != snapshot.WeekUsedPercent)
        {
            snapshot = snapshot with
            {
                FiveHourUsedPercent = normalizedFiveHour,
                FiveHourResetAtLocal = normalizedFiveHour is null ? null : snapshot.FiveHourResetAtLocal,
                WeekUsedPercent = normalizedWeek,
                WeekResetAtLocal = normalizedWeek is null ? null : snapshot.WeekResetAtLocal
            };
        }

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

    private static CodexQuotaEstimate BuildQuotaEstimate(
        RateLimitSnapshot snapshot,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        return new CodexQuotaEstimate(
            snapshot.TimestampLocal,
            snapshot.LimitId,
            snapshot.LimitName,
            BuildQuotaWindowEstimate("5h", snapshot.FiveHour, now, cancellationToken: cancellationToken),
            BuildQuotaWindowEstimate("1周", snapshot.Week, now, cancellationToken: cancellationToken));
    }

    private static CodexQuotaEstimate BuildQuotaEstimate(
        CodexQuotaSnapshot snapshot,
        DateTimeOffset now,
        bool includeLiveToday = true,
        CancellationToken cancellationToken = default)
    {
        return new CodexQuotaEstimate(
            snapshot.SnapshotLocal,
            snapshot.LimitId,
            snapshot.LimitName,
            BuildQuotaWindowEstimate(
                "5h",
                ToRateLimitWindow(snapshot.FiveHourUsedPercent, 5 * 60, snapshot.FiveHourResetAtLocal),
                now,
                includeLiveToday,
                cancellationToken),
            BuildQuotaWindowEstimate(
                "1周",
                ToRateLimitWindow(snapshot.WeekUsedPercent, 7 * 24 * 60, snapshot.WeekResetAtLocal),
                now,
                includeLiveToday,
                cancellationToken));
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
        bool includeLiveToday = true,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (snapshot is not { } window || window.WindowMinutes <= 0)
        {
            return null;
        }

        var windowStart = window.ResetAtLocal is null
            ? now.AddMinutes(-window.WindowMinutes)
            : window.ResetAtLocal.Value.AddMinutes(-window.WindowMinutes);
        if (windowStart > now)
        {
            windowStart = now.AddMinutes(-window.WindowMinutes);
        }

        var usage = ReadRangeFromDetailRows(windowStart, now, includeLiveToday, cancellationToken);
        var usedCost = CodexModelCost.Estimate(usage).KnownCost;
        var estimatedCostLimit = CodexModelCost.EstimateQuotaValue(usage, window.UsedPercent);
        // A mixed-model subscription does not have a fixed token capacity.
        long? estimatedTokenLimit = null;
        return new CodexQuotaWindowEstimate(
            label,
            window.UsedPercent,
            window.WindowMinutes,
            windowStart,
            now,
            window.ResetAtLocal,
            usage,
            usedCost,
            estimatedCostLimit,
            estimatedTokenLimit);
    }

    public static TokenUsageSummary ReadRangeFromDetailRows(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        bool includeLiveToday = true,
        CancellationToken cancellationToken = default)
    {
        startLocal = startLocal.ToOffset(BeijingOffset);
        endLocal = endLocal.ToOffset(BeijingOffset);
        var summary = new TokenUsageSummary
        {
            StartLocal = startLocal,
            EndLocal = endLocal
        };
        var dailyBuckets = new Dictionary<DateOnly, TokenUsageBucket>();

        for (var segmentStart = startLocal; segmentStart < endLocal;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var nextDay = StartOfDay(segmentStart).AddDays(1);
            var segmentEnd = nextDay < endLocal ? nextDay : endLocal;
            var rows = includeLiveToday
                ? ReadDetailRows(
                    segmentStart,
                    segmentEnd,
                    includeLiveToday: true,
                    cancellationToken: cancellationToken)
                : ReadCachedDetailRows(segmentStart, segmentEnd, cancellationToken);
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
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
            lock (QuotaHistoryCacheSync)
            {
                EnsureQuotaHistoryCacheLoaded(path);
                if (QuotaHistoryKeyCache.Contains(historyKey))
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
                QuotaHistorySnapshotCache.Add(snapshot);
                QuotaHistoryKeyCache.Add(historyKey);
                UpdateQuotaHistoryCacheFileState(path);
            }
        }
        catch
        {
            // Quota history is best-effort; the live estimate should still render if persistence fails.
        }
    }

    private static object? ToQuotaHistoryWindow(RateLimitWindowSnapshot? window)
    {
        if (window is not { } value)
        {
            return null;
        }

        return new
        {
            usedPercent = value.UsedPercent,
            windowMinutes = value.WindowMinutes,
            resetAtLocal = value.ResetAtLocal
        };
    }

    private static void EnsureQuotaHistoryCacheLoaded(
        string path,
        CancellationToken cancellationToken = default)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            ResetQuotaHistoryCache(path);
            return;
        }

        if (string.Equals(quotaHistoryCachedPath, path, StringComparison.OrdinalIgnoreCase) &&
            quotaHistoryCachedLength == info.Length &&
            quotaHistoryCachedWriteTimeUtc == info.LastWriteTimeUtc)
        {
            return;
        }

        QuotaHistorySnapshotCache.Clear();
        QuotaHistoryKeyCache.Clear();
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(line) || TryReadQuotaHistorySnapshot(line) is not { } snapshot)
                {
                    continue;
                }

                QuotaHistorySnapshotCache.Add(snapshot);
                QuotaHistoryKeyCache.Add(new QuotaHistoryKey(
                    snapshot.TimestampLocal,
                    NormalizeLimitId(snapshot.LimitId)));
            }

            UpdateQuotaHistoryCacheFileState(path);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ResetQuotaHistoryCache(path);
            throw;
        }
    }

    private static void ResetQuotaHistoryCache(string path)
    {
        QuotaHistorySnapshotCache.Clear();
        QuotaHistoryKeyCache.Clear();
        quotaHistoryCachedPath = path;
        quotaHistoryCachedLength = -1;
        quotaHistoryCachedWriteTimeUtc = default;
    }

    private static void UpdateQuotaHistoryCacheFileState(string path)
    {
        var info = new FileInfo(path);
        info.Refresh();
        quotaHistoryCachedPath = path;
        quotaHistoryCachedLength = info.Exists ? info.Length : -1;
        quotaHistoryCachedWriteTimeUtc = info.Exists ? info.LastWriteTimeUtc : default;
    }

    private static string NormalizeLimitId(string? limitId)
    {
        return string.IsNullOrWhiteSpace(limitId)
            ? "unknown"
            : limitId.Trim();
    }

    private static string GetQuotaHistoryPath()
    {
        return Path.Combine(MonitorCachePaths.LocalAppData, CacheFolder, QuotaHistoryFileName);
    }

    private static IReadOnlyList<RateLimitSnapshot> ReadQuotaHistorySnapshots(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        CancellationToken cancellationToken = default)
    {
        var path = GetQuotaHistoryPath();
        lock (QuotaHistoryCacheSync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureQuotaHistoryCacheLoaded(path, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return QuotaHistorySnapshotCache
                .Where(snapshot => snapshot.TimestampLocal >= startLocal && snapshot.TimestampLocal < endLocal)
                .ToList();
        }
    }

    private static RateLimitSnapshot? TryReadQuotaHistorySnapshot(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("snapshotLocal", out var snapshotElement) ||
                !DateTimeOffset.TryParse(snapshotElement.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var timestamp))
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
        if (!QuotaPercentRules.IsValid(usedPercent) || windowMinutes <= 0)
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

        return new RateLimitWindowSnapshot(usedPercent!.Value, windowMinutes, resetAt);
    }

    public static void WarmHistoricalDays(
        IEnumerable<DateTimeOffset> daysLocal,
        CancellationToken cancellationToken = default,
        Action<DateTimeOffset>? dayCompleted = null,
        Action<int, int>? fileProgress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var todayStart = StartOfDay(DateTimeOffset.UtcNow.ToOffset(BeijingOffset));
        var days = daysLocal
            .Select(day => StartOfDay(day.ToOffset(BeijingOffset)))
            .Where(day => day < todayStart)
            .Distinct()
            .OrderByDescending(day => day)
            .ToList();
        if (days.Count == 0)
        {
            return;
        }

        var incompleteDates = GetIncompleteHistoricalDays(days[^1], days[0], cancellationToken)
            .Select(day => DateOnly.FromDateTime(day.DateTime))
            .ToHashSet();
        foreach (var day in days)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!incompleteDates.Contains(DateOnly.FromDateTime(day.DateTime)))
            {
                dayCompleted?.Invoke(day);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        days = days.Where(day => incompleteDates.Contains(DateOnly.FromDateTime(day.DateTime))).ToList();
        if (days.Count == 0)
        {
            return;
        }

        var startLocal = days[^1];
        var endLocal = days[0].AddDays(1);
        var eventsByDay = days.ToDictionary(
            day => DateOnly.FromDateTime(day.DateTime),
            _ => new List<TokenUsageEvent>());
        var files = new List<string>();
        foreach (var root in GetLogRoots())
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var file in EnumerateJsonlFiles(root, startLocal, endLocal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                files.Add(file);
            }
        }

        // Read each source once for the entire pending history. A historical
        // day's mtime filter also includes every later log, so scanning days
        // separately repeatedly reads the same (potentially very large) files.
        // Retain only token events for requested days, never the JSONL text.
        var isComplete = true;
        fileProgress?.Invoke(0, files.Count);
        for (var index = 0; index < files.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileEvents = new List<TokenUsageEvent>();
            if (!ReadEventFile(files[index], startLocal, endLocal, fileEvents, cancellationToken))
            {
                isComplete = false;
            }

            foreach (var item in fileEvents)
            {
                var date = DateOnly.FromDateTime(item.Timestamp.ToOffset(BeijingOffset).DateTime);
                if (eventsByDay.TryGetValue(date, out var dayEvents))
                {
                    dayEvents.Add(item);
                }
            }
            fileProgress?.Invoke(index + 1, files.Count);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var cache = UsageCacheStore.Load(CacheFolder);
        foreach (var dayStart in days)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var date = DateOnly.FromDateTime(dayStart.DateTime);
            // Deduplication is scoped to a day, matching usage_events' key and
            // the existing per-day reader when a turn continues past midnight.
            var events = UsageEventMerger.Merge(cache.GetDetailEvents(date, cancellationToken)
                .Concat(eventsByDay[date]));
            eventsByDay.Remove(date);
            var dayEnd = dayStart.AddDays(1);
            if (events.Count == 0 && cache.TryGet(date, out var legacyBucket) && legacyBucket.Events > 0)
            {
                // Keep legacy summary-only data if the original source is no
                // longer present, but do not claim its missing details are complete.
                cache.Put(legacyBucket, isComplete: false, scannedThroughLocal: dayEnd.AddTicks(-1),
                    cancellationToken: cancellationToken, propagateErrors: true);
                continue;
            }

            var bucket = CreateBucketFromEvents(dayStart, events);
            cache.Put(bucket, isComplete, dayEnd.AddTicks(-1), events,
                cancellationToken: cancellationToken, propagateErrors: true);
            cancellationToken.ThrowIfCancellationRequested();
            if (isComplete && GetIncompleteHistoricalDays(dayStart, dayStart, cancellationToken).Count == 0)
            {
                dayCompleted?.Invoke(dayStart);
            }
        }
    }

    public static TokenUsageSummary ReadRange(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        bool includeLiveToday = true,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        startLocal = startLocal.ToOffset(BeijingOffset);
        endLocal = endLocal.ToOffset(BeijingOffset);
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
            cancellationToken.ThrowIfCancellationRequested();
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

            // Quota cycles usually start partway through a day. A complete day
            // with all event details can answer that slice precisely, including
            // imported events. Reopening rollouts here both rescans history on
            // every cycle refresh and omits events that exist on another device.
            if (!fullHistoricalDay && dayStart < todayStart &&
                cache.TryGetRecord(date, out var boundaryRecord) &&
                boundaryRecord.IsComplete && boundaryRecord.DetailEventCount == boundaryRecord.Events)
            {
                var boundarySummary = cache.ReadRange(clippedStart, clippedEnd, cancellationToken);
                foreach (var bucket in boundarySummary.DailyBuckets)
                {
                    AddBucketToSummary(summary, dailyBuckets, bucket);
                }
                continue;
            }

            if (fullCachedDay && cache.TryGet(date, out var cachedBucket))
            {
                AddBucketToSummary(summary, dailyBuckets, cachedBucket);
            }

            if (fullHistoricalDay)
            {
                if (cache.TryGetRecord(date, out var record) &&
                    record.IsComplete &&
                    record.DetailEventCount == record.Events)
                {
                    continue;
                }

                // An incomplete historical record can retain an end-of-day
                // watermark after a schema change or a failed source scan.
                // Its previous coverage is no longer proof that the counters
                // are current, so rebuild the whole day and merge its details.
                AddScanRange(scanRanges, dayStart, dayEnd, cacheHistoricalDays: true);
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
            cancellationToken.ThrowIfCancellationRequested();
            var scanned = ReadRangeUncached(scanRange.StartLocal, scanRange.EndLocal, cancellationToken);
            foreach (var bucket in scanned.Summary.DailyBuckets)
            {
                AddBucketToSummary(summary, dailyBuckets, bucket);
            }

            for (var dayStart = StartOfDay(scanRange.StartLocal); dayStart < scanRange.EndLocal; dayStart = dayStart.AddDays(1))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var date = DateOnly.FromDateTime(dayStart.DateTime);
                var isToday = dayStart == todayStart;
                if (!scanRange.CacheHistoricalDays && !isToday)
                {
                    continue;
                }

                var scannedBucket = scanned.Summary.DailyBuckets.FirstOrDefault(item =>
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
                var isComplete = scanRange.CacheHistoricalDays && dayStart < todayStart && scanned.IsComplete;
                IReadOnlyList<TokenUsageEvent>? detailEvents = null;
                var replaceDetailEvents = true;
                var hasDetailEvents = cache.HasDetailEvents(date, cancellationToken);
                if (hasDetailEvents)
                {
                    var detailStart = isToday
                        ? dayStart
                        : Max(dayStart, scanRange.StartLocal);
                    var detailEnd = Min(dayStart.AddDays(1), scanRange.EndLocal);
                    var newEventsResult = ReadEventsUncached(
                        detailStart,
                        detailEnd,
                        useLiveCursor: true,
                        cancellationToken: cancellationToken);
                    detailEvents = UsageEventMerger.Merge(cache.GetDetailEvents(date, cancellationToken)
                        .Concat(newEventsResult.Events)
                        .Where(item => item.Timestamp >= dayStart && item.Timestamp < dayStart.AddDays(1)));
                    mergedBucket = CreateBucketFromEvents(dayStart, detailEvents);
                    replaceDetailEvents = true;
                    isComplete &= newEventsResult.IsComplete;
                }
                else if (scanRange.CacheHistoricalDays && scannedBucket.Events > 0)
                {
                    // A legacy cache may contain only a daily aggregate. For
                    // a full historical rescan, prefer newly observed source
                    // events when available instead of adding them to the old
                    // aggregate and double-counting the day.
                    mergedBucket = scannedBucket;
                }

                cache.Put(
                    mergedBucket,
                    isComplete,
                    scannedThrough,
                    detailEvents,
                    replaceDetailEvents,
                    cancellationToken);
                dailyBuckets[date] = mergedBucket;
                cacheChanged = true;
            }
        }

        if (cacheChanged)
        {
            cancellationToken.ThrowIfCancellationRequested();
            cache.Save();
            summary = new TokenUsageSummary
            {
                StartLocal = startLocal,
                EndLocal = endLocal
            };
            foreach (var bucket in dailyBuckets.Values)
            {
                AddBucketValues(summary, bucket);
            }
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
        bool includeLiveToday = true,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        startLocal = startLocal.ToOffset(BeijingOffset);
        endLocal = endLocal.ToOffset(BeijingOffset);
        var dayStart = StartOfDay(startLocal);
        var date = DateOnly.FromDateTime(dayStart.DateTime);
        var now = DateTimeOffset.UtcNow.ToOffset(BeijingOffset);
        var todayStart = StartOfDay(now);
        var dayEnd = dayStart.AddDays(1);
        var effectiveEndLocal = dayStart == todayStart && includeLiveToday
            ? Min(endLocal, now)
            : endLocal;
        var cache = UsageCacheStore.Load(CacheFolder);
        var cachedEvents = cache.GetDetailEvents(date, cancellationToken).ToList();

        if (dayStart == todayStart && !includeLiveToday)
        {
            return ToDetailBuckets(cachedEvents.Where(item => item.Timestamp >= startLocal && item.Timestamp < endLocal));
        }

        if (cache.TryGetRecord(date, out var record) && (cachedEvents.Count > 0 || record.Events == 0))
        {
            var effectiveScannedThrough = dayStart == todayStart
                ? GetEffectiveLiveScannedThrough(record, now)
                : record.ScannedThroughLocal;
            var hasCompleteDetails = cachedEvents.Count == record.Events;
            var hasCompleteCoverage = hasCompleteDetails && (record.IsComplete ||
                                      dayStart == todayStart && effectiveScannedThrough is not null &&
                                      effectiveScannedThrough.Value >= effectiveEndLocal.AddTicks(-1));
            if (hasCompleteCoverage)
            {
                return ToDetailBuckets(cachedEvents.Where(item => item.Timestamp >= startLocal && item.Timestamp < endLocal));
            }

            if (dayStart < todayStart || includeLiveToday)
            {
                var replaceDetails = !hasCompleteDetails || dayStart < todayStart && !record.IsComplete;
                var scanStart = replaceDetails || effectiveScannedThrough is null
                    ? replaceDetails ? dayStart : startLocal
                    : Max(startLocal, effectiveScannedThrough.Value.AddTicks(1));
                var scanEnd = dayStart < todayStart ? dayEnd : effectiveEndLocal;
                if (scanStart < scanEnd)
                {
                    // Always give a fresh live cursor the full day. This self-heals a cache
                    // produced by an older build that advanced past future-dated JSONL rows.
                    var newEventsResult = ReadEventsUncached(
                        dayStart,
                        scanEnd,
                        useLiveCursor: true,
                        cancellationToken: cancellationToken);
                    var mergedEvents = UsageEventMerger.Merge(cachedEvents
                        .Concat(newEventsResult.Events)
                        .Where(item => item.Timestamp >= dayStart && item.Timestamp < dayEnd));
                    var mergedBucket = CreateBucketFromEvents(dayStart, mergedEvents);
                    var isComplete = dayStart < todayStart && scanEnd >= dayEnd && newEventsResult.IsComplete;
                    cache.Put(
                        mergedBucket,
                        isComplete,
                        scanEnd.AddTicks(-1),
                        replaceDetails ? mergedEvents : newEventsResult.Events,
                        replaceDetailEvents: replaceDetails,
                        cancellationToken: cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    cache.Save();
                    cachedEvents = mergedEvents.ToList();
                }
            }

            return ToDetailBuckets(cachedEvents.Where(item => item.Timestamp >= startLocal && item.Timestamp < endLocal));
        }

        var fullEventsResult = ReadEventsUncached(startLocal, endLocal, cancellationToken: cancellationToken);
        var fullEvents = UsageEventMerger.Merge(cachedEvents.Concat(fullEventsResult.Events));
        var fullBucket = CreateBucketFromEvents(dayStart, fullEvents);
        var completeHistoricalDay = dayStart < todayStart && startLocal == dayStart && endLocal >= dayEnd && fullEventsResult.IsComplete;
        if (startLocal == dayStart)
        {
            cache.Put(
                fullBucket,
                completeHistoricalDay,
                endLocal.AddTicks(-1),
                fullEvents,
                cancellationToken: cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
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
        DateTimeOffset endLocal,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        startLocal = startLocal.ToOffset(BeijingOffset);
        endLocal = endLocal.ToOffset(BeijingOffset);
        return ToDetailBuckets(ReadEventsUncached(startLocal, endLocal, cancellationToken: cancellationToken).Events);
    }

    private static UsageRangeScanResult ReadRangeUncached(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        CancellationToken cancellationToken)
    {
        PruneLiveFileState(startLocal, endLocal);
        var summary = new TokenUsageSummary
        {
            StartLocal = startLocal,
            EndLocal = endLocal
        };
        var dailyBuckets = new Dictionary<DateOnly, TokenUsageBucket>();
        var isComplete = true;

        foreach (var root in GetLogRoots())
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var file in EnumerateJsonlFiles(root, startLocal, endLocal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!ReadFile(file, startLocal, endLocal, summary, dailyBuckets, cancellationToken))
                {
                    isComplete = false;
                }
            }
        }

        summary.DailyBuckets.AddRange(
            dailyBuckets.Values
                .OrderBy(bucket => bucket.StartLocal)
                .Where(bucket => bucket.Events > 0));

        return new UsageRangeScanResult(summary, isComplete);
    }

    private static UsageEventScanResult ReadEventsUncached(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        bool useLiveCursor = false,
        CancellationToken cancellationToken = default)
    {
        var events = new List<TokenUsageEvent>();
        var isComplete = true;
        var incremental = useLiveCursor && IsLiveRange(startLocal, endLocal);
        if (incremental)
        {
            PruneLiveFileState(startLocal, endLocal);
        }

        foreach (var root in GetLogRoots())
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var file in EnumerateJsonlFiles(root, startLocal, endLocal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (incremental)
                {
                    if (!ReadEventFileIncremental(file, startLocal, endLocal, events, cancellationToken))
                    {
                        isComplete = false;
                    }
                }
                else
                {
                    if (!ReadEventFile(file, startLocal, endLocal, events, cancellationToken))
                    {
                        isComplete = false;
                    }
                }
            }
        }

        return new UsageEventScanResult(UsageEventMerger.Merge(events).ToList(), isComplete);
    }

    private static bool ReadEventFile(
        string file,
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        List<TokenUsageEvent> events,
        CancellationToken cancellationToken)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var replayFilter = new SubagentReplayFilter();
            while (reader.ReadLine() is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!replayFilter.ShouldReadTokenCount(line))
                {
                    continue;
                }

                var usageEvent = TryReadUsageEvent(line, startLocal, endLocal, replayFilter.ModelId, replayFilter.ServiceTier);
                if (usageEvent is not null)
                {
                    events.Add(usageEvent);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }

        return true;
    }

    private static bool ReadEventFileIncremental(
        string file,
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        List<TokenUsageEvent> events,
        CancellationToken cancellationToken)
    {
        var replayFilter = UsageReplayFilters.GetOrAdd(file, static _ => new SubagentReplayFilter());
        return UsageTailReader.ReadNewLinesWhile(file, startLocal, line =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (SubagentReplayFilter.TryReadRecordTimestamp(line) is { } timestamp && timestamp >= endLocal)
            {
                return false;
            }

            if (!replayFilter.ShouldReadTokenCount(line))
            {
                return true;
            }

            var usageEvent = TryReadUsageEvent(line, startLocal, endLocal, replayFilter.ModelId, replayFilter.ServiceTier);
            if (usageEvent is not null)
            {
                events.Add(usageEvent);
            }

            return true;
        }, cancellationToken, onRestart: () =>
        {
            replayFilter = new SubagentReplayFilter();
            UsageReplayFilters[file] = replayFilter;
        });
    }

    private static IReadOnlyList<TokenUsageBucket> ToDetailBuckets(IEnumerable<TokenUsageEvent> events)
    {
        return UsageEventMerger.Merge(events)
            .Select(item =>
            {
                var bucket = new TokenUsageBucket { StartLocal = item.Timestamp };
                bucket.Add(item);
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
            bucket.Add(item);
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
        var codexHome = UsageLogPaths.GetOverrideRoot(UsageSource.Codex) ?? OverrideCodexHome;
        if (string.IsNullOrWhiteSpace(codexHome))
        {
            codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        }
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

    private static IEnumerable<string> EnumerateJsonlFiles(
        string root,
        DateTimeOffset startLocal,
        DateTimeOffset endLocal)
    {
        // Session contents can extend beyond their preserved file mtime (including
        // copied history). Historical batches inspect all files once; only live
        // tail polling uses mtime to narrow the candidates.
        var incremental = IsLiveRange(startLocal, endLocal);
        var startUtc = startLocal.Subtract(TimeSpan.FromDays(1)).UtcDateTime;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true
        };

        foreach (var file in Directory.EnumerateFiles(root, "*.jsonl", options))
        {
            if (!incremental)
            {
                yield return file;
                continue;
            }

            FileInfo info;
            try
            {
                info = new FileInfo(file);
            }
            catch
            {
                continue;
            }

            if (info.LastWriteTimeUtc >= startUtc ||
                UsageTailReader.IsTracked(file) ||
                QuotaTailReader.IsTracked(file))
            {
                yield return file;
            }
        }
    }

    private static IReadOnlyList<RateLimitSnapshot> ReadLatestRateLimitSnapshots(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        CancellationToken cancellationToken = default)
    {
        return ReadRateLimitSnapshots(startLocal, endLocal, cancellationToken).Snapshots
            .GroupBy(GetRateLimitKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(item => item.TimestampLocal).First())
            .OrderByDescending(item => item.TimestampLocal)
            .ToList();
    }

    private static RateLimitScanResult ReadRateLimitSnapshots(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        CancellationToken cancellationToken = default,
        Action<int, int>? fileProgress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshots = new List<RateLimitSnapshot>();
        var isComplete = true;
        var incremental = IsLiveRange(startLocal, endLocal);
        if (incremental)
        {
            PruneLiveFileState(startLocal, endLocal);
        }

        var files = GetLogRoots()
            .SelectMany(root => EnumerateJsonlFiles(root, startLocal, endLocal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        fileProgress?.Invoke(0, files.Count);
        var completed = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (incremental)
                {
                    if (!ReadRateLimitFileIncremental(file, startLocal, endLocal, snapshots, cancellationToken))
                    {
                        isComplete = false;
                    }
                    continue;
                }

                try
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream);
                    var replayFilter = new SubagentReplayFilter();
                    while (reader.ReadLine() is { } line)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!replayFilter.ShouldReadTokenCount(line) ||
                            !line.Contains("\"rate_limits\"", StringComparison.Ordinal))
                        {
                            continue;
                        }

                        if (TryReadRateLimitSnapshot(line, startLocal, endLocal) is not { } snapshot)
                        {
                            continue;
                        }

                        snapshots.Add(snapshot);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // Ignore files that are actively being written or are not readable.
                    isComplete = false;
                }
            }
            finally
            {
                fileProgress?.Invoke(++completed, files.Count);
            }
        }

        return new RateLimitScanResult(snapshots, isComplete);
    }

    private static bool ReadRateLimitFileIncremental(
        string file,
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        List<RateLimitSnapshot> snapshots,
        CancellationToken cancellationToken)
    {
        var replayFilter = QuotaReplayFilters.GetOrAdd(file, static _ => new SubagentReplayFilter());
        return QuotaTailReader.ReadNewLinesWhile(file, startLocal, line =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (SubagentReplayFilter.TryReadRecordTimestamp(line) is { } timestamp && timestamp >= endLocal)
            {
                return false;
            }

            if (!replayFilter.ShouldReadTokenCount(line) ||
                !line.Contains("\"rate_limits\"", StringComparison.Ordinal))
            {
                return true;
            }

            if (TryReadRateLimitSnapshot(line, startLocal, endLocal) is { } snapshot)
            {
                snapshots.Add(snapshot);
            }

            return true;
        }, cancellationToken);
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

    private static void PruneLiveFileState(DateTimeOffset startLocal, DateTimeOffset endLocal)
    {
        if (!IsLiveRange(startLocal, endLocal))
        {
            return;
        }

        // Keep the same one-day look-back used by EnumerateJsonlFiles. A file
        // appended after this point will get a fresh mtime and be discovered on
        // the next pass, while inactive historical files no longer pin state.
        var cutoffUtc = startLocal.Subtract(TimeSpan.FromDays(1)).UtcDateTime;
        UsageTailReader.PruneBeforeUtc(cutoffUtc);
        QuotaTailReader.PruneBeforeUtc(cutoffUtc);
        PruneReplayFiltersBeforeUtc(UsageReplayFilters, cutoffUtc);
        PruneReplayFiltersBeforeUtc(QuotaReplayFilters, cutoffUtc);
    }

    private static void PruneReplayFiltersBeforeUtc(
        ConcurrentDictionary<string, SubagentReplayFilter> filters,
        DateTime cutoffUtc)
    {
        foreach (var pair in filters)
        {
            bool shouldRemove;
            try
            {
                var info = new FileInfo(pair.Key);
                shouldRemove = !info.Exists || info.LastWriteTimeUtc < cutoffUtc;
            }
            catch
            {
                continue;
            }

            if (shouldRemove)
            {
                ((ICollection<KeyValuePair<string, SubagentReplayFilter>>)filters).Remove(pair);
            }
        }
    }

    private static RateLimitSnapshot? SelectDisplayedQuotaSnapshot(IReadOnlyList<RateLimitSnapshot> snapshots)
    {
        return snapshots
            .Where(IsDisplayedQuotaSnapshot)
            .OrderByDescending(item => item.TimestampLocal)
            .Select(item => (RateLimitSnapshot?)item)
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
        if (!QuotaPercentRules.IsValid(usedPercent) || windowMinutes <= 0)
        {
            return null;
        }

        DateTimeOffset? resetAt = null;
        var resetSeconds = GetInt64(window, "resets_at");
        if (resetSeconds > 0)
        {
            resetAt = DateTimeOffset.FromUnixTimeSeconds(resetSeconds).ToOffset(BeijingOffset);
        }

        return new RateLimitWindowSnapshot(usedPercent!.Value, windowMinutes, resetAt);
    }

    private static (RateLimitWindowSnapshot? FiveHour, RateLimitWindowSnapshot? Week) ClassifyRateLimitWindows(
        RateLimitWindowSnapshot? first,
        RateLimitWindowSnapshot? second)
    {
        var windows = new[] { first, second }
            .Where(window => window is not null)
            .Select(window => window!.Value)
            .GroupBy(window => window.WindowMinutes)
            .Select(group => group.First())
            .ToList();

        // Field position is not stable, but window duration is. Other windows
        // (notably the 30-day reset-card window) are not 5h/7d quota data.
        return (
            windows
                .Where(window => IsFiveHourWindow(window.WindowMinutes))
                .Select(window => (RateLimitWindowSnapshot?)window)
                .FirstOrDefault(),
            windows
                .Where(window => IsWeeklyWindow(window.WindowMinutes))
                .Select(window => (RateLimitWindowSnapshot?)window)
                .FirstOrDefault());
    }

    internal static bool IsFiveHourWindow(int windowMinutes) => windowMinutes == FiveHourWindowMinutes;

    internal static bool IsWeeklyWindow(int windowMinutes) => windowMinutes == WeeklyWindowMinutes;

    private static bool ReadFile(
        string file,
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        TokenUsageSummary summary,
        Dictionary<DateOnly, TokenUsageBucket> dailyBuckets,
        CancellationToken cancellationToken)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var replayFilter = new SubagentReplayFilter();
            while (reader.ReadLine() is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!replayFilter.ShouldReadTokenCount(line))
                {
                    continue;
                }

                ReadLine(line, startLocal, endLocal, summary, dailyBuckets, replayFilter.ModelId, replayFilter.ServiceTier);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }

        return true;
    }

    private static void ReadLine(
        string line,
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        TokenUsageSummary summary,
        Dictionary<DateOnly, TokenUsageBucket> dailyBuckets,
        string? modelId, string? serviceTier)
    {
        var usageEvent = TryReadUsageEvent(line, startLocal, endLocal, modelId, serviceTier);
        if (usageEvent is null)
        {
            return;
        }

        summary.Add(usageEvent);

        var dayKey = DateOnly.FromDateTime(usageEvent.Timestamp.DateTime);
        if (!dailyBuckets.TryGetValue(dayKey, out var bucket))
        {
            bucket = new TokenUsageBucket
            {
                StartLocal = new DateTimeOffset(dayKey.Year, dayKey.Month, dayKey.Day, 0, 0, 0, BeijingOffset)
            };
            dailyBuckets[dayKey] = bucket;
        }

        bucket.Add(usageEvent);
    }

    private static TokenUsageEvent? TryReadUsageEvent(
        string line,
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        string? modelId, string? serviceTier)
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
            var cacheWrite = GetInt64(usage, "cache_write_input_tokens");
            var output = GetInt64(usage, "output_tokens");
            var reasoning = GetInt64(usage, "reasoning_output_tokens");
            var total = GetInt64(usage, "total_tokens");
            if (total == 0)
            {
                total = TokenCountMath.AddNonNegative(input, output);
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
                string.IsNullOrWhiteSpace(key) ? null : $"codex:{key}",
                cacheWrite,
                ReadModelId(usage) ?? ReadModelId(info) ?? ReadModelId(payload) ?? modelId,
                ReadServiceTier(usage) ?? ReadServiceTier(info) ?? ReadServiceTier(payload) ?? serviceTier);
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadServiceTier(JsonElement element) =>
        element.TryGetProperty("service_tier", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim().ToLowerInvariant() : null;

    private static string? ReadModelId(JsonElement element)
    {
        foreach (var name in new[] { "model", "model_name" })
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(value.GetString())) return value.GetString();
        return null;
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
