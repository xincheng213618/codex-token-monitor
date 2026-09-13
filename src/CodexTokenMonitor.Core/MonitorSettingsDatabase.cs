using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;

namespace CodexTokenMonitor;

internal static class MonitorSettingsDatabase
{
    // Purchases and reset-card settings are user data, not rebuildable log caches.
    public static string Path => System.IO.Path.Combine(
        MonitorCachePaths.LocalAppData, "CodexTokenMonitor", "monitor-settings.sqlite3");

    public static SqliteConnection OpenConnection(string path, bool create = true)
    {
        var directory = System.IO.Path.GetDirectoryName(path);
        if (create && !string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
            Pooling = true
        }.ToString());
        try
        {
            connection.Open();
            ExecuteNonQuery(connection, null, "PRAGMA busy_timeout=5000;");
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public static void ExecuteNonQuery(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}

/// <summary>
/// Retains successful settings snapshots per database path. An operation reads
/// each table once, while a failed read stays distinguishable from empty user data.
/// </summary>
internal sealed class MonitorSettingsTable<T>(
    string tableName,
    string schemaSql,
    Func<SqliteConnection, SqliteTransaction?, IReadOnlyList<T>> readRecords,
    Action<SqliteConnection, SqliteTransaction, IReadOnlyList<T>> writeRecords,
    Func<IReadOnlyList<T>> defaults,
    Func<IEnumerable<T>, IReadOnlyList<T>> cloneRecords,
    Func<IReadOnlyList<T>, IReadOnlyList<T>> normalizeRecords)
{
    private readonly ConcurrentDictionary<string, TableState> states = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<T> Load(bool forceReload = false)
    {
        var path = MonitorSettingsDatabase.Path;
        var state = states.GetOrAdd(path, _ => new TableState());
        lock (state.Sync)
        {
            var operation = CacheOperationDiagnostics.CurrentOperation;
            if (!forceReload && operation is not null && state.OperationReads.TryGetValue(operation, out var snapshot))
            {
                if (snapshot.Failure is not null)
                    CacheOperationDiagnostics.Report(path, $"{tableName}.Load", snapshot.Failure);
                return cloneRecords(snapshot.Records);
            }
            if (!forceReload && operation is null && state.Healthy)
                return CloneLastGood(state);

            try
            {
                using var connection = OpenConnection(path, state);
                EnsureInitialized(connection, state);
                var result = readRecords(connection, null);
                state.LastGood = cloneRecords(result);
                state.Healthy = true;
                RememberOperation(state, operation, failure: null);
                return cloneRecords(result);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ReportFailure(state, path, $"{tableName}.Load", ex);
                return CloneLastGood(state);
            }
        }
    }

    public void Save(IReadOnlyList<T> records)
    {
        var path = MonitorSettingsDatabase.Path;
        var state = states.GetOrAdd(path, _ => new TableState());
        lock (state.Sync)
        {
            try
            {
                var normalized = normalizeRecords(records);
                using var connection = OpenConnection(path, state);
                EnsureInitialized(connection, state);
                using var transaction = connection.BeginTransaction();
                // Validate every stored row before replacing anything. A settings
                // read failure must never turn a fallback snapshot into a save.
                _ = readRecords(connection, transaction);
                writeRecords(connection, transaction, normalized);
                transaction.Commit();
                state.LastGood = cloneRecords(normalized);
                state.Healthy = true;
                RememberOperation(state, CacheOperationDiagnostics.CurrentOperation, failure: null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ReportFailure(state, path, $"{tableName}.Save", ex);
                throw;
            }
        }
    }

    private static SqliteConnection OpenConnection(string path, TableState state)
    {
        // Once observed, a settings database is user data even if its first read
        // failed. Its disappearance must not create a replacement with defaults.
        state.KnownDatabase |= File.Exists(path);
        var connection = MonitorSettingsDatabase.OpenConnection(path, create: !state.KnownDatabase);
        state.KnownDatabase = true;
        return connection;
    }

    private void EnsureInitialized(SqliteConnection connection, TableState state)
    {
        if (state.Initialized) return;
        using var transaction = connection.BeginTransaction();
        using var exists = connection.CreateCommand();
        exists.Transaction = transaction;
        exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name";
        exists.Parameters.AddWithValue("$name", tableName);
        var tableExists = Convert.ToInt64(exists.ExecuteScalar()) != 0;
        if (!tableExists && state.KnownTable)
            throw new InvalidDataException($"已保存的配置表 {tableName} 缺失，未创建替代数据。");
        if (tableExists) state.KnownTable = true;
        MonitorSettingsDatabase.ExecuteNonQuery(connection, transaction, schemaSql);
        // An existing empty table is an intentional user configuration. Only a
        // newly created table receives the initial examples.
        if (!tableExists) writeRecords(connection, transaction, defaults());
        transaction.Commit();
        state.KnownTable = true;
        state.Initialized = true;
    }

    private IReadOnlyList<T> CloneLastGood(TableState state) =>
        state.LastGood is null ? Array.Empty<T>() : cloneRecords(state.LastGood);

    private static void ReportFailure(TableState state, string path, string operation, Exception exception)
    {
        state.Initialized = false;
        state.Healthy = false;
        RememberOperation(state, CacheOperationDiagnostics.CurrentOperation, exception);
        CacheOperationDiagnostics.Report(path, operation, exception);
    }

    private static void RememberOperation(TableState state, CacheOperationDiagnostics? operation, Exception? failure)
    {
        if (operation is null) return;
        state.OperationReads.Remove(operation);
        state.OperationReads.Add(operation, new ReadSnapshot(state.LastGood ?? Array.Empty<T>(), failure));
    }

    private sealed record ReadSnapshot(IReadOnlyList<T> Records, Exception? Failure);

    private sealed class TableState
    {
        public object Sync { get; } = new();
        public bool KnownDatabase { get; set; }
        public bool KnownTable { get; set; }
        public bool Initialized { get; set; }
        public bool Healthy { get; set; }
        public IReadOnlyList<T>? LastGood { get; set; }
        public ConditionalWeakTable<CacheOperationDiagnostics, ReadSnapshot> OperationReads { get; } = new();
    }
}
