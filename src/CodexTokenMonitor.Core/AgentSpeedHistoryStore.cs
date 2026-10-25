using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace CodexTokenMonitor;

internal sealed record AgentSpeedHistoryRow(DateOnly Day, UsageSource Source, string Model, string? Mode,
    long Requests, long MeasuredRequests, long Unfinalized, long InputTokens, long OutputTokens,
    long MeasuredInput, long MeasuredOutput, long MeasuredMilliseconds, double? OutputPeak, double? TotalPeak)
{
    public double? Average(AgentSpeedMetric metric) => MeasuredMilliseconds > 0
        ? (MeasuredOutput + (metric == AgentSpeedMetric.TotalThroughput ? (double)MeasuredInput : 0)) * 1000 / MeasuredMilliseconds : null;
}
internal sealed record AgentSpeedHistoryFile(string Path, string Signature);
internal sealed record AgentSpeedHistoryFileResult(IReadOnlyList<AgentSpeedResponse> Responses, bool Complete);
internal sealed record AgentSpeedHistoryProgress(UsageSource Source, int Completed, int Total, int Unreadable);
internal sealed record AgentSpeedHistoryIndexResult(int Read, int Reused, int Unreadable);

/// <summary>Persists file/day/model/mode aggregates and hashed ownership, never transcript text or request rows.</summary>
internal sealed class AgentSpeedHistoryStore
{
    private const int ParserVersion = 1;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Writers = new(StringComparer.OrdinalIgnoreCase);
    public static string DefaultPath => System.IO.Path.Combine(MonitorCachePaths.LocalAppData, "CodexTokenMonitor", "speed-history.sqlite3");
    public string Path { get; }
    private SemaphoreSlim Writer => Writers.GetOrAdd(Path, _ => new(1));
    public AgentSpeedHistoryStore(string? path = null) => Path = System.IO.Path.GetFullPath(path ?? DefaultPath);

    public AgentSpeedHistoryIndexResult Index(UsageSource source, IReadOnlyList<AgentSpeedHistoryFile> files,
        Func<AgentSpeedHistoryFile, CancellationToken, AgentSpeedHistoryFileResult> read,
        DateTimeOffset now, CancellationToken token, IProgress<AgentSpeedHistoryProgress>? progress = null)
        => Index(source, files, (file, _, cancellation) => read(file, cancellation), now, token, progress);

    public AgentSpeedHistoryIndexResult Index(UsageSource source, IReadOnlyList<AgentSpeedHistoryFile> files,
        Func<AgentSpeedHistoryFile, Func<string, bool>, CancellationToken, AgentSpeedHistoryFileResult> read,
        DateTimeOffset now, CancellationToken token, IProgress<AgentSpeedHistoryProgress>? progress = null)
    {
        Writer.Wait(token);
        try
        {
            using var connection = Open(create: true); EnsureSchema(connection);
            var saved = new Dictionary<string, (string Signature, int Version, long? NextFuture)>(StringComparer.Ordinal);
            using (var command = Command(connection, null, "SELECT file_hash, signature, parser_version, next_future_ms FROM speed_files WHERE source=$source", ("$source", (int)source)))
            using (var rows = command.ExecuteReader())
                while (rows.Read()) saved[rows.GetString(0)] = (rows.GetString(1), rows.GetInt32(2), rows.IsDBNull(3) ? null : rows.GetInt64(3));
            var changed = files.Where(file => !saved.TryGetValue(FileHash(file.Path), out var old) || old.Signature != file.Signature ||
                old.Version != ParserVersion || old.NextFuture <= now.ToUnixTimeMilliseconds()).ToArray();
            // A request can start in an unchanged Qoder segment and finish in a new one.
            if (source == UsageSource.Qoder && changed.Length > 0)
            {
                var sessions = changed.Select(file => System.IO.Path.GetDirectoryName(file.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                changed = files.Where(file => sessions.Contains(System.IO.Path.GetDirectoryName(file.Path))).ToArray();
            }
            var changing = changed.Select(file => FileHash(file.Path)).ToHashSet(StringComparer.Ordinal);
            using var transaction = connection.BeginTransaction();
            Execute(connection, transaction, """
                CREATE TEMP TABLE staged (
                  file_hash TEXT NOT NULL, ordering INTEGER NOT NULL, key_hash TEXT NOT NULL, day TEXT NOT NULL,
                  model TEXT NOT NULL, mode TEXT NOT NULL, input INTEGER NOT NULL, output INTEGER NOT NULL,
                  measured_input INTEGER NOT NULL, measured_output INTEGER NOT NULL, measured_ms INTEGER NOT NULL,
                  measured INTEGER NOT NULL, unfinalized INTEGER NOT NULL, PRIMARY KEY(file_hash,key_hash));
                CREATE TEMP TABLE changed (file_hash TEXT PRIMARY KEY, signature TEXT NOT NULL, next_future_ms INTEGER);
                CREATE INDEX staged_keys ON staged(key_hash,ordering);
                """);
            var readCount = 0; var failures = 0;
            foreach (var file in changed)
            {
                token.ThrowIfCancellationRequested();
                var fileHash = FileHash(file.Path);
                using var stagedOwner = Command(connection, transaction, "SELECT file_hash FROM staged WHERE key_hash=$key ORDER BY ordering LIMIT 1", ("$key", ""));
                using var savedOwner = Command(connection, transaction, "SELECT file_hash FROM speed_owners WHERE source=$source AND key_hash=$key", ("$source", (int)source), ("$key", ""));
                bool CanUse(string key)
                {
                    var hash = Hash(key); stagedOwner.Parameters["$key"].Value = hash;
                    if (stagedOwner.ExecuteScalar() is string staged) return staged == fileHash;
                    savedOwner.Parameters["$key"].Value = hash;
                    return savedOwner.ExecuteScalar() is not string savedFile || savedFile == fileHash || changing.Contains(savedFile);
                }
                var result = read(file, CanUse, token); readCount++;
                if (!result.Complete)
                {
                    failures++;
                    var preserve = result.Responses.Count == 0;
                    if (saved.TryGetValue(fileHash, out var previous))
                    {
                        // A partially rewritten file cannot replace a previously complete contribution.
                        preserve |= !previous.Signature.StartsWith("partial:", StringComparison.Ordinal);
                        var keys = result.Responses.Select(response => Hash(response.NativeResponseId ?? response.Key)).ToHashSet(StringComparer.Ordinal);
                        using var oldKeys = Command(connection, transaction, "SELECT key_hash FROM speed_owners WHERE source=$source AND file_hash=$file", ("$source", (int)source), ("$file", fileHash));
                        using var owned = oldKeys.ExecuteReader();
                        while (owned.Read()) if (!keys.Contains(owned.GetString(0))) preserve = true;
                    }
                    if (preserve) { progress?.Report(new(source, readCount, changed.Length, failures)); continue; }
                    // A new incomplete file still contributes its complete prefix. Its signature stays
                    // deliberately unmatched so the next index retries the unfinished tail.
                }
                long? nextFuture = null;
                foreach (var response in result.Responses)
                {
                    token.ThrowIfCancellationRequested();
                    if (response.Source != source || response.End < DateTimeOffset.MinValue.AddDays(2) || response.End > DateTimeOffset.MaxValue.AddDays(-2)) continue;
                    if (response.End > now)
                    {
                        var future = response.End.ToUnixTimeMilliseconds(); nextFuture = nextFuture is null ? future : Math.Min(nextFuture.Value, future); continue;
                    }
                    var duration = response.Final ? response.DurationSeconds : null;
                    var key = Hash(response.NativeResponseId ?? response.Key);
                    Execute(connection, transaction, """
                        INSERT OR REPLACE INTO staged VALUES
                        ($file,$ordering,$key,$day,$model,$mode,$input,$output,$measured_input,$measured_output,$ms,$measured,$unfinalized)
                        """, ("$file", fileHash), ("$ordering", readCount), ("$key", key),
                        ("$day", DateOnly.FromDateTime(response.End.ToOffset(CodexUsageReader.BeijingOffset).DateTime).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                        ("$model", response.Model), ("$mode", response.ExecutionMode ?? ""),
                        ("$input", Math.Max(0, response.InputTokens)), ("$output", Math.Max(0, response.OutputTokens)),
                        ("$measured_input", duration is null ? 0 : Math.Max(0, response.InputTokens)),
                        ("$measured_output", duration is null ? 0 : Math.Max(0, response.OutputTokens)),
                        ("$ms", duration is { } seconds ? (long)Math.Round(seconds * 1000) : 0),
                        ("$measured", duration is null ? 0 : 1), ("$unfinalized", response.Final ? 0 : 1));
                }
                if (!result.Complete && saved.ContainsKey(fileHash))
                {
                    // A partially rewritten prefix can retain the same request keys but smaller usage.
                    // Only advance an existing partial contribution when its totals do not regress.
                    using var regressed = Command(connection, transaction, """
                        SELECT (SELECT COALESCE(SUM(input),0) FROM staged WHERE file_hash=$file) <
                               (SELECT COALESCE(SUM(input),0) FROM speed_daily WHERE source=$source AND file_hash=$file)
                            OR (SELECT COALESCE(SUM(output),0) FROM staged WHERE file_hash=$file) <
                               (SELECT COALESCE(SUM(output),0) FROM speed_daily WHERE source=$source AND file_hash=$file)
                        """, ("$file", fileHash), ("$source", (int)source));
                    if (Convert.ToInt32(regressed.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
                    {
                        Execute(connection, transaction, "DELETE FROM staged WHERE file_hash=$file", ("$file", fileHash));
                        continue;
                    }
                }
                Execute(connection, transaction, "INSERT INTO changed VALUES($file,$signature,$future)", ("$file", fileHash),
                    ("$signature", result.Complete ? file.Signature : "partial:" + file.Signature), ("$future", nextFuture));
                progress?.Report(new(source, readCount, changed.Length, failures));
            }
            token.ThrowIfCancellationRequested();
            // Release all successfully changed owners together before reclaiming keys. This also handles
            // requests transferred between replaced files, regardless of their discovery order.
            Execute(connection, transaction, """
                DELETE FROM speed_owners WHERE source=$source AND file_hash IN (SELECT file_hash FROM changed);
                INSERT OR IGNORE INTO speed_owners(source,key_hash,file_hash)
                  SELECT $source,key_hash,file_hash FROM staged ORDER BY ordering,key_hash;
                DELETE FROM speed_daily WHERE source=$source AND file_hash IN (SELECT file_hash FROM changed);
                INSERT INTO speed_daily
                  SELECT s.file_hash,$source,s.day,s.model,s.mode,COUNT(*),SUM(s.measured),SUM(s.unfinalized),
                    SUM(s.input),SUM(s.output),SUM(s.measured_input),SUM(s.measured_output),SUM(s.measured_ms)
                  FROM staged s JOIN speed_owners o ON o.source=$source AND o.key_hash=s.key_hash AND o.file_hash=s.file_hash
                  GROUP BY s.file_hash,s.day,s.model,s.mode;
                INSERT INTO speed_files(file_hash,source,signature,parser_version,next_future_ms)
                  SELECT file_hash,$source,signature,$version,next_future_ms FROM changed WHERE 1
                  ON CONFLICT(file_hash,source) DO UPDATE SET signature=excluded.signature,parser_version=excluded.parser_version,next_future_ms=excluded.next_future_ms;
                """, ("$source", (int)source), ("$version", ParserVersion));
            transaction.Commit();
            return new(readCount, files.Count - changed.Length, failures);
        }
        finally { Writer.Release(); }
    }

    public IReadOnlyList<AgentSpeedHistoryRow> Read(DateOnly start, DateOnly end, UsageSource? source = null)
    {
        if (end < start) throw new ArgumentOutOfRangeException(nameof(end));
        if (!File.Exists(Path)) return [];
        using var connection = Open(create: false); CheckVersion(connection);
        using var command = Command(connection, null, """
            SELECT d.day,d.source,d.model,d.mode,SUM(d.requests),SUM(d.measured_requests),SUM(d.unfinalized),
              SUM(d.input),SUM(d.output),SUM(d.measured_input),SUM(d.measured_output),SUM(d.measured_ms),
              p.output_peak,p.total_peak
            FROM speed_daily d LEFT JOIN speed_peaks p ON p.day=d.day AND p.source=d.source
            WHERE d.day >= $start AND d.day <= $end AND ($source IS NULL OR d.source=$source)
            GROUP BY d.day,d.source,d.model,d.mode ORDER BY d.day DESC,d.source,SUM(d.output) DESC
            """, ("$start", start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), ("$end", end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), ("$source", source is { } selected ? (int)selected : null));
        var result = new List<AgentSpeedHistoryRow>();
        using var rows = command.ExecuteReader();
        while (rows.Read())
            result.Add(new(DateOnly.ParseExact(rows.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture), (UsageSource)rows.GetInt32(1),
                rows.GetString(2), rows.GetString(3) is { Length: > 0 } mode ? mode : null,
                rows.GetInt64(4), rows.GetInt64(5), rows.GetInt64(6), rows.GetInt64(7), rows.GetInt64(8),
                rows.GetInt64(9), rows.GetInt64(10), rows.GetInt64(11), rows.IsDBNull(12) ? null : rows.GetDouble(12), rows.IsDBNull(13) ? null : rows.GetDouble(13)));
        return result;
    }

    public bool TryRecordPeak(UsageSource source, AgentSpeedSnapshot snapshot)
    {
        if (snapshot.MeasuredPeak is not { } peak || !double.IsFinite(peak) || peak < 0 || !Writer.Wait(0)) return false;
        try
        {
            using var connection = Open(create: true); EnsureSchema(connection);
            var day = DateOnly.FromDateTime(snapshot.DayStart.DateTime).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var output = snapshot.Metric == AgentSpeedMetric.Output ? (double?)peak : null;
            var total = snapshot.Metric == AgentSpeedMetric.TotalThroughput ? (double?)peak : null;
            Execute(connection, null, """
                INSERT INTO speed_peaks VALUES($day,$source,$output,$total)
                ON CONFLICT(day,source) DO UPDATE SET
                  output_peak=CASE WHEN excluded.output_peak IS NULL THEN output_peak WHEN output_peak IS NULL THEN excluded.output_peak ELSE MAX(output_peak,excluded.output_peak) END,
                  total_peak=CASE WHEN excluded.total_peak IS NULL THEN total_peak WHEN total_peak IS NULL THEN excluded.total_peak ELSE MAX(total_peak,excluded.total_peak) END
                """, ("$day", day), ("$source", (int)source), ("$output", output), ("$total", total));
            return true;
        }
        catch (SqliteException) { return false; }
        catch (IOException) { return false; }
        catch (InvalidDataException) { return false; }
        finally { Writer.Release(); }
    }

    public string BackupAndReset(CancellationToken token)
    {
        Writer.Wait(token);
        try
        {
            var backup = Path + ".backup-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
            var moved = new List<string>();
            try
            {
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                    if (File.Exists(Path + suffix)) { File.Move(Path + suffix, backup + suffix); moved.Add(suffix); }
            }
            catch
            {
                foreach (var suffix in moved.AsEnumerable().Reverse()) File.Move(backup + suffix, Path + suffix);
                throw;
            }
            using var connection = Open(create: true); EnsureSchema(connection);
            return backup;
        }
        finally { Writer.Release(); }
    }

    private SqliteConnection Open(bool create)
    {
        if (create) Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path,
            Mode = create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 1 }.ToString());
        try { connection.Open(); return connection; } catch { connection.Dispose(); throw; }
    }
    private static void CheckVersion(SqliteConnection connection)
    {
        using var version = Command(connection, null, "PRAGMA user_version");
        if (Convert.ToInt32(version.ExecuteScalar(), CultureInfo.InvariantCulture) is not (0 or 1))
            throw new InvalidDataException("速度历史库版本不受支持，原文件已保留。");
    }
    private static void EnsureSchema(SqliteConnection connection)
    {
        CheckVersion(connection);
        Execute(connection, null, """
            PRAGMA journal_mode=WAL;
            PRAGMA user_version=1;
            CREATE TABLE IF NOT EXISTS speed_files(file_hash TEXT NOT NULL,source INTEGER NOT NULL,signature TEXT NOT NULL,parser_version INTEGER NOT NULL,next_future_ms INTEGER,PRIMARY KEY(file_hash,source));
            CREATE TABLE IF NOT EXISTS speed_owners(source INTEGER NOT NULL,key_hash TEXT NOT NULL,file_hash TEXT NOT NULL,PRIMARY KEY(source,key_hash));
            CREATE INDEX IF NOT EXISTS speed_owners_file ON speed_owners(source,file_hash);
            CREATE TABLE IF NOT EXISTS speed_daily(file_hash TEXT NOT NULL,source INTEGER NOT NULL,day TEXT NOT NULL,model TEXT NOT NULL,mode TEXT NOT NULL,
              requests INTEGER NOT NULL,measured_requests INTEGER NOT NULL,unfinalized INTEGER NOT NULL,input INTEGER NOT NULL,output INTEGER NOT NULL,
              measured_input INTEGER NOT NULL,measured_output INTEGER NOT NULL,measured_ms INTEGER NOT NULL,PRIMARY KEY(file_hash,source,day,model,mode));
            CREATE INDEX IF NOT EXISTS speed_daily_dates ON speed_daily(day,source);
            CREATE TABLE IF NOT EXISTS speed_peaks(day TEXT NOT NULL,source INTEGER NOT NULL,output_peak REAL,total_peak REAL,PRIMARY KEY(day,source));
            """);
    }
    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
    private static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    { using var command = Command(connection, transaction, sql, parameters); command.ExecuteNonQuery(); }
    private static string FileHash(string file) => Hash(System.IO.Path.GetFullPath(file).ToUpperInvariant());
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
