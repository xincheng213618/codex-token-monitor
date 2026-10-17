namespace CodexTokenMonitor;

// Derived data lives beside the source cache and is removed with it. Persist
// token counters rather than formatted costs so price/plan changes stay live.
internal sealed class QuotaEstimateHistoryStore : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly Dictionary<(string Source, string Date), long> versions;
    private readonly (long Length, long WriteTicks) initialHistory;
    private readonly Dictionary<string, (string Version, string Json)> usage = new();
    private readonly List<(CodexQuotaCycle Period, string Version, TokenUsageSummary Usage)> pending = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PreferredObjectCreationHandling = System.Text.Json.Serialization.JsonObjectCreationHandling.Populate
    };
    private static string Path => UsageCacheStore.GetCachePath("CodexTokenMonitor");
    private static string HistoryPath => System.IO.Path.Combine(MonitorCachePaths.LocalAppData,
        "CodexTokenMonitor", "quota-history-v2.jsonl");

    public bool HasSavedPeriods { get; private set; }
    public int UsageCacheHits { get; private set; }

    private QuotaEstimateHistoryStore(SqliteConnection connection)
    {
        this.connection = connection;
        MonitorSettingsDatabase.ExecuteNonQuery(connection, null, """
            CREATE TABLE IF NOT EXISTS quota_estimate_periods (
                id INTEGER PRIMARY KEY CHECK(id = 1), version TEXT NOT NULL,
                periods_json TEXT NOT NULL, history_length INTEGER NOT NULL,
                history_write_ticks INTEGER NOT NULL, history_end TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS quota_estimate_usage (
                period_key TEXT PRIMARY KEY, version TEXT NOT NULL, usage_json TEXT NOT NULL
            );
            """);
        versions = ReadVersions(connection);
        initialHistory = HistoryState();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT period_key, version, usage_json FROM quota_estimate_usage";
        using var reader = command.ExecuteReader();
        while (reader.Read()) usage[reader.GetString(0)] = (reader.GetString(1), reader.GetString(2));
    }

    public static QuotaEstimateHistoryStore? Open()
    {
        SqliteConnection? connection = null;
        try
        {
            UsageCacheStore.Load();
            QuotaSnapshotCacheStore.Load("CodexTokenMonitor");
            connection = MonitorSettingsDatabase.OpenConnection(Path, create: false);
            return new(connection);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            connection?.Dispose();
            CacheOperationDiagnostics.Report(Path, nameof(Open), ex);
            return null;
        }
    }

    // Day writes, imports, and deletions all pass through these source tables.
    // One revision per day write avoids a trigger on every individual event.
    internal static void EnsureTrackingSchema(SqliteConnection connection, string table, string source)
    {
        MonitorSettingsDatabase.ExecuteNonQuery(connection, null, """
            CREATE TABLE IF NOT EXISTS quota_estimate_revisions (
                source TEXT NOT NULL, date TEXT NOT NULL, revision INTEGER NOT NULL,
                PRIMARY KEY(source, date)
            );
            """);
        foreach (var action in new[] { "INSERT", "UPDATE", "DELETE" })
        {
            var row = action == "DELETE" ? "OLD" : "NEW";
            // Identifiers come exclusively from the two fixed initialization calls.
            MonitorSettingsDatabase.ExecuteNonQuery(connection, null, $"""
                CREATE TRIGGER IF NOT EXISTS quota_estimate_{source}_{action}
                AFTER {action} ON {table} BEGIN
                    INSERT INTO quota_estimate_revisions(source, date, revision)
                    VALUES ('{source}', {row}.date, 1)
                    ON CONFLICT(source, date) DO UPDATE SET revision = revision + 1;
                END;
                """);
        }
    }

    private static Dictionary<(string Source, string Date), long> ReadVersions(
        SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT source, date, revision FROM quota_estimate_revisions ORDER BY source, date";
        using var reader = command.ExecuteReader();
        var result = new Dictionary<(string, string), long>();
        while (reader.Read()) result[(reader.GetString(0), reader.GetString(1))] = reader.GetInt64(2);
        return result;
    }

    private static string Version(Dictionary<(string Source, string Date), long> versions, string source,
        DateTimeOffset? start = null, DateTimeOffset? end = null)
    {
        var startDate = start?.ToOffset(CodexUsageReader.BeijingOffset).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var endDate = end?.ToOffset(CodexUsageReader.BeijingOffset).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        // Bump the prefix if the derived period/summary format changes.
        return "1|" + string.Join(";", versions.Where(item => item.Key.Source == source &&
                (startDate is null || string.CompareOrdinal(item.Key.Date, startDate) >= 0) &&
                (endDate is null || string.CompareOrdinal(item.Key.Date, endDate) <= 0))
            .OrderBy(item => item.Key.Date).Select(item => $"{item.Key.Date}:{item.Value.ToString(CultureInfo.InvariantCulture)}"));
    }

    private static (long Length, long WriteTicks) HistoryState()
    {
        var info = new FileInfo(HistoryPath);
        return info.Exists ? (info.Length, info.LastWriteTimeUtc.Ticks) : (-1, 0);
    }

    public IReadOnlyList<CodexQuotaCycle>? ReadPeriods(CodexQuotaEstimate quota)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT version, periods_json, history_length, history_write_ticks FROM quota_estimate_periods WHERE id = 1";
        using var reader = command.ExecuteReader();
        HasSavedPeriods = reader.Read();
        if (!HasSavedPeriods) return null;
        CodexQuotaCycle[]? periods;
        try { periods = JsonSerializer.Deserialize<CodexQuotaCycle[]>(reader.GetString(1)); }
        catch (JsonException) { return null; }
        if (periods is null || periods.Count(item => item.IsCurrent) != 1) return null;
        var current = periods.Single(item => item.IsCurrent);
        if (current is null || quota.Week?.ResetAtLocal is not { } reset ||
            !CodexQuotaCycleReader.IsSameQuotaReset(current.ResetAt, reset) ||
            quota.SnapshotLocal < current.PeriodStart ||
            quota.Week.UsedPercent + 2m < (current.MaxWeekUsedPercent ?? 0m) ||
            reader.GetString(0) != Version(versions, "quota", end: current.PeriodStart) ||
            HistoryState() != (reader.GetInt64(2), reader.GetInt64(3))) return null;
        return periods;
    }

    public void SavePeriods(IReadOnlyList<CodexQuotaCycle> periods, CancellationToken token)
    {
        if (periods.Count(item => item.IsCurrent) != 1) return;
        var current = periods.Single(item => item.IsCurrent);
        token.ThrowIfCancellationRequested();
        var version = Version(versions, "quota", end: current.PeriodStart);
        using var transaction = connection.BeginTransaction();
        if (version != Version(ReadVersions(connection, transaction), "quota", end: current.PeriodStart)) return;
        var history = HistoryState();
        if (history != initialHistory) return;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO quota_estimate_periods VALUES (1, $version, $periods, $length, $ticks, $end)
            ON CONFLICT(id) DO UPDATE SET version = excluded.version, periods_json = excluded.periods_json,
                history_length = excluded.history_length, history_write_ticks = excluded.history_write_ticks,
                history_end = excluded.history_end
            """;
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$periods", JsonSerializer.Serialize(periods));
        command.Parameters.AddWithValue("$length", history.Length);
        command.Parameters.AddWithValue("$ticks", history.WriteTicks);
        command.Parameters.AddWithValue("$end", current.PeriodStart.ToString("O"));
        command.ExecuteNonQuery();
        token.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    // Routine live history appends do not change finished cycles. Only advance
    // the file stamp if its previous stamp still matches, so external edits
    // cannot be hidden by a subsequent append from this process.
    public static void HistoryAppended(DateTimeOffset timestamp, long previousLength, long previousTicks)
    {
        if (!File.Exists(Path)) return;
        using var connection = MonitorSettingsDatabase.OpenConnection(Path, create: false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'quota_estimate_periods'";
        if (command.ExecuteScalar() is null) return;
        var state = HistoryState();
        command.CommandText = """
            UPDATE quota_estimate_periods SET history_length = $length, history_write_ticks = $ticks
            WHERE history_length = $previous_length AND history_write_ticks = $previous_ticks
              AND history_end <= $timestamp
            """;
        command.Parameters.AddWithValue("$length", state.Length);
        command.Parameters.AddWithValue("$ticks", state.WriteTicks);
        command.Parameters.AddWithValue("$previous_length", previousLength);
        command.Parameters.AddWithValue("$previous_ticks", previousTicks);
        command.Parameters.AddWithValue("$timestamp", timestamp.ToOffset(CodexUsageReader.BeijingOffset).ToString("O"));
        command.ExecuteNonQuery();
    }

    private static string PeriodKey(CodexQuotaCycle period) => $"{period.PeriodStart:O}/{period.PeriodEnd:O}";

    public TokenUsageSummary ReadUsage(CodexQuotaCycle period, UsageCacheStore source, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var key = PeriodKey(period);
        var version = Version(versions, "usage", period.PeriodStart, period.PeriodEnd.AddTicks(-1));
        if (usage.TryGetValue(key, out var cached) && cached.Version == version)
        {
            try
            {
                if (JsonSerializer.Deserialize<TokenUsageSummary>(cached.Json, JsonOptions) is { } summary &&
                    summary.StartLocal == period.PeriodStart && summary.EndLocal == period.PeriodEnd)
                {
                    UsageCacheHits++;
                    return summary;
                }
            }
            catch (JsonException) { /* A damaged derived entry is rebuilt from source. */ }
        }
        var result = source.ReadRange(period.PeriodStart, period.PeriodEnd, token);
        pending.Add((period, version, result));
        return result;
    }

    public void Flush(CancellationToken token)
    {
        if (pending.Count == 0) return;
        using var transaction = connection.BeginTransaction();
        var currentVersions = ReadVersions(connection, transaction);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO quota_estimate_usage VALUES ($key, $version, $usage)
            ON CONFLICT(period_key) DO UPDATE SET version = excluded.version, usage_json = excluded.usage_json
            """;
        command.Parameters.AddWithValue("$key", "");
        command.Parameters.AddWithValue("$version", "");
        command.Parameters.AddWithValue("$usage", "");
        foreach (var entry in pending)
        {
            token.ThrowIfCancellationRequested();
            if (entry.Version != Version(currentVersions, "usage", entry.Period.PeriodStart, entry.Period.PeriodEnd.AddTicks(-1))) continue;
            command.Parameters["$key"].Value = PeriodKey(entry.Period);
            command.Parameters["$version"].Value = entry.Version;
            command.Parameters["$usage"].Value = JsonSerializer.Serialize(entry.Usage);
            command.ExecuteNonQuery();
        }
        token.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    public void Dispose() => connection.Dispose();
}
