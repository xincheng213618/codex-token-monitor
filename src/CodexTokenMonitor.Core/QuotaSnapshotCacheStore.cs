namespace CodexTokenMonitor;

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
    private readonly CacheDatabaseState database;

    private QuotaSnapshotCacheStore(string cachePath)
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

        if (!EnsureAvailable())
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

    private static DateTimeOffset Max(DateTimeOffset first, DateTimeOffset second)
    {
        return first >= second ? first : second;
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
        if (!EnsureAvailable())
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            database.ReportFailure(ex);
            return false;
        }
    }

    public IReadOnlyList<CodexQuotaSnapshot> GetSnapshots(
        DateOnly date,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!EnsureAvailable())
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            database.ReportFailure(ex);
            return Array.Empty<CodexQuotaSnapshot>();
        }
    }

    public IReadOnlyList<CodexQuotaSnapshot> GetAllSnapshots(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!EnsureAvailable())
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            database.ReportFailure(ex);
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
        if (importedSnapshots.Count == 0)
        {
            return 0;
        }
        EnsureAvailable(propagateErrors: true);

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
        if (!EnsureAvailable() || startLocal >= endLocal)
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            database.ReportFailure(ex);
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
        if (!EnsureAvailable() || startLocal >= endLocal)
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            database.ReportFailure(ex);
            return Array.Empty<CodexQuotaSnapshot>();
        }
    }

    public void PutTimelineSnapshots(
        IReadOnlyList<CodexQuotaSnapshot> snapshots,
        IReadOnlyDictionary<DateTimeOffset, (DateTimeOffset? Before, DateTimeOffset? After)> sources,
        CancellationToken cancellationToken = default)
    {
        if (!EnsureAvailable() || snapshots.Count == 0)
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            database.ReportFailure(ex);
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
        if (!EnsureAvailable())
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            database.ReportFailure(ex);
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            database.ReportFailure(ex);
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
        cancellationToken.ThrowIfCancellationRequested();
        if (!EnsureAvailable(propagateErrors))
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            database.ReportFailure(ex);
            if (propagateErrors) throw;
            // Quota snapshot cache is an optimization; live parsing can still work without it.
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
