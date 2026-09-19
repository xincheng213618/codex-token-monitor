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
    private readonly CacheDatabaseState database;

    private UsageCacheStore(string cachePath)
    {
        this.cachePath = cachePath;
        database = new CacheDatabaseState(cachePath);
        EnsureAvailable(operation: nameof(InitializeDatabase));
    }

    private bool EnsureAvailable(
        bool propagateErrors = false,
        [System.Runtime.CompilerServices.CallerMemberName] string operation = "")
    {
        return database.EnsureAvailable(InitializeDatabase, propagateErrors, operation);
    }

    public static string GetCachePath(string folderName)
    {
        return Path.Combine(MonitorCachePaths.LocalAppData, folderName,
            folderName == "CodexTokenMonitor" ? "token-cache-v4.sqlite3"
                : folderName == "ZCodeTokenMonitor" ? "token-cache-v5.sqlite3"
                : CacheFileName);
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

        if (!EnsureAvailable())
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            database.ReportFailure(ex);
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
        if (!EnsureAvailable()) return Array.Empty<string>();
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

        if (!EnsureAvailable() || startLocal >= endLocal)
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            database.ReportFailure(ex);
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
        if (!EnsureAvailable() || startLocal >= endLocal)
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            database.ReportFailure(ex);
            return Array.Empty<TokenUsageBucket>();
        }
    }

    public bool TryGetRecord(DateOnly date, out CachedDayRecord record)
    {
        record = null!;
        if (!EnsureAvailable())
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            database.ReportFailure(ex);
            return false;
        }
    }

    public IReadOnlyList<TokenUsageEvent> GetDetailEvents(
        DateOnly date,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!EnsureAvailable())
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            database.ReportFailure(ex);
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
        if (!EnsureAvailable() || startLocal >= endLocal)
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            database.ReportFailure(ex);
            return Array.Empty<TokenUsageEvent>();
        }
    }

    public IReadOnlyList<TokenUsageEvent> GetAllDetailEvents()
    {
        if (!EnsureAvailable())
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            database.ReportFailure(ex);
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
        if (!EnsureAvailable(propagateErrors: true) ||
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
        if (importedEvents.Count == 0)
        {
            return 0;
        }
        EnsureAvailable(propagateErrors: true);

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
        if (!EnsureAvailable())
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            database.ReportFailure(ex);
            return false;
        }
    }

    public bool DeleteDay(DateOnly date)
    {
        if (!EnsureAvailable())
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            database.ReportFailure(ex);
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
        cancellationToken.ThrowIfCancellationRequested();
        if (!EnsureAvailable(propagateErrors))
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            database.ReportFailure(ex);
            if (propagateErrors) throw;
            // Cache writes are best-effort; usage calculation should keep working without them.
        }
    }

    public void Save()
    {
        // SQLite writes are committed in Put().
    }

    private void InitializeDatabase()
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
        // ZCode logs carry model.modelId; re-scan days whose zcode events were
        // cached without models, then never again for this migration. Events
        // are kept: the re-scan merge enriches matching keys, and rows whose
        // logs no longer exist must survive. Resetting scanned_through lets
        // the next live scan rebuild today too.
        ExecuteNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS cache_maintenance (name TEXT PRIMARY KEY);
            UPDATE usage_days SET is_complete = 0, scanned_through_local = NULL
            WHERE date IN (SELECT DISTINCT date FROM usage_events
                           WHERE (model_id IS NULL OR model_id = '') AND event_key LIKE 'zcode:%')
              AND NOT EXISTS (SELECT 1 FROM cache_maintenance WHERE name = 'zcode-model-context-v1');
            INSERT OR IGNORE INTO cache_maintenance VALUES ('zcode-model-context-v1');
            """);
        // WorkBuddy logs carry providerData.model; same one-shot re-scan for
        // days whose workbuddy events were cached before model attribution.
        ExecuteNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS cache_maintenance (name TEXT PRIMARY KEY);
            UPDATE usage_days SET is_complete = 0, scanned_through_local = NULL
            WHERE date IN (SELECT DISTINCT date FROM usage_events
                           WHERE (model_id IS NULL OR model_id = '') AND event_key LIKE 'workbuddy:%')
              AND NOT EXISTS (SELECT 1 FROM cache_maintenance WHERE name = 'workbuddy-model-context-v1');
            INSERT OR IGNORE INTO cache_maintenance VALUES ('workbuddy-model-context-v1');
            """);
        // Claude logs carry message.model; same one-shot re-scan for days
        // whose claude events were cached before model attribution.
        ExecuteNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS cache_maintenance (name TEXT PRIMARY KEY);
            UPDATE usage_days SET is_complete = 0, scanned_through_local = NULL
            WHERE date IN (SELECT DISTINCT date FROM usage_events
                           WHERE (model_id IS NULL OR model_id = '') AND event_key LIKE 'claude:%')
              AND NOT EXISTS (SELECT 1 FROM cache_maintenance WHERE name = 'claude-model-context-v1');
            INSERT OR IGNORE INTO cache_maintenance VALUES ('claude-model-context-v1');
            """);
        DeleteLegacyDerivedFiles(cachePath);
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
        return JsonSerializer.Deserialize<Dictionary<string, TokenUsageBucket>>(reader.GetString(ordinal)) ?? new();
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
            // Pooling has handed out disposed native handles under parallel
            // test load; store I/O is serialized by the shared gate anyway.
            Pooling = false
        };
        var connection = new SqliteConnection(builder.ToString());
        try
        {
            connection.Open();
            ExecuteNonQuery(connection, "PRAGMA busy_timeout=5000;");
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
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
