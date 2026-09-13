using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace CodexTokenMonitor;

internal static class QuotaModelCapacityCalibrationStore
{
    private static readonly ConcurrentDictionary<string, DatabaseState> States = new(StringComparer.OrdinalIgnoreCase);

    public static void Upsert(
        string planName,
        CodexQuotaCycle period,
        IReadOnlyCollection<QuotaModelCapacityEstimate> estimates)
    {
        if (estimates.Count == 0)
        {
            return;
        }

        Execute(nameof(Upsert), connection =>
        {
            using var transaction = connection.BeginTransaction();
            foreach (var estimate in estimates)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO quota_model_calibrations (
                        plan_name, model_id, period_start, period_end,
                        average_full_quota_cost, minimum_full_quota_cost,
                        maximum_full_quota_cost, sample_count, updated_at)
                    VALUES (
                        $plan_name, $model_id, $period_start, $period_end,
                        $average, $minimum, $maximum, $sample_count, $updated_at)
                    ON CONFLICT(plan_name, model_id, period_start) DO UPDATE SET
                        period_end = excluded.period_end,
                        average_full_quota_cost = excluded.average_full_quota_cost,
                        minimum_full_quota_cost = excluded.minimum_full_quota_cost,
                        maximum_full_quota_cost = excluded.maximum_full_quota_cost,
                        sample_count = excluded.sample_count,
                        updated_at = excluded.updated_at
                    """;
                command.Parameters.AddWithValue("$plan_name", planName);
                command.Parameters.AddWithValue("$model_id", CodexModelCost.NormalizeModelId(estimate.ModelId));
                command.Parameters.AddWithValue("$period_start", FormatDateTimeOffset(period.PeriodStart));
                command.Parameters.AddWithValue("$period_end", FormatDateTimeOffset(period.PeriodEnd));
                command.Parameters.AddWithValue("$average", FormatDecimal(estimate.AverageFullQuotaCost));
                command.Parameters.AddWithValue("$minimum", FormatDecimal(estimate.MinimumFullQuotaCost));
                command.Parameters.AddWithValue("$maximum", FormatDecimal(estimate.MaximumFullQuotaCost));
                command.Parameters.AddWithValue("$sample_count", estimate.BandCount);
                command.Parameters.AddWithValue("$updated_at", FormatDateTimeOffset(DateTimeOffset.UtcNow));
                command.ExecuteNonQuery();
            }
            transaction.Commit();
            return true;
        });
    }

    public static IReadOnlyList<QuotaModelCapacityEstimate> LoadExact(
        string planName,
        DateTimeOffset periodStart,
        QuotaModelCapacitySource source)
    {
        return Execute(nameof(LoadExact), connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT model_id, average_full_quota_cost, minimum_full_quota_cost,
                       maximum_full_quota_cost, sample_count
                FROM quota_model_calibrations
                WHERE plan_name = $plan_name AND period_start = $period_start
                ORDER BY model_id
                """;
            command.Parameters.AddWithValue("$plan_name", planName);
            command.Parameters.AddWithValue("$period_start", FormatDateTimeOffset(periodStart));
            var result = new List<QuotaModelCapacityEstimate>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new QuotaModelCapacityEstimate(
                    reader.GetString(0),
                    reader.GetInt32(4),
                    ParseDecimal(reader.GetString(1)),
                    ParseDecimal(reader.GetString(2)),
                    ParseDecimal(reader.GetString(3)),
                    source,
                    periodStart));
            }
            return result;
        });
    }

    public static IReadOnlyList<QuotaModelCapacityEstimate> LoadLatestBefore(
        string planName,
        DateTimeOffset periodStart,
        IReadOnlyCollection<string> modelIds,
        QuotaModelCapacitySource source)
    {
        var requestedModels = modelIds
            .Select(CodexModelCost.NormalizeModelId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (requestedModels.Count == 0)
        {
            return Array.Empty<QuotaModelCapacityEstimate>();
        }

        return Execute(nameof(LoadLatestBefore), connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT model_id, period_start, average_full_quota_cost,
                       minimum_full_quota_cost, maximum_full_quota_cost, sample_count
                FROM quota_model_calibrations
                WHERE plan_name = $plan_name
                ORDER BY model_id, period_start DESC
                """;
            command.Parameters.AddWithValue("$plan_name", planName);
            var latest = new Dictionary<string, QuotaModelCapacityEstimate>(StringComparer.OrdinalIgnoreCase);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var modelId = CodexModelCost.NormalizeModelId(reader.GetString(0));
                var calibrationStart = ParseDateTimeOffset(reader.GetString(1));
                if (!requestedModels.Contains(modelId) || calibrationStart >= periodStart ||
                    latest.TryGetValue(modelId, out var existing) && existing.CalibrationPeriodStart >= calibrationStart)
                {
                    continue;
                }

                latest[modelId] = new QuotaModelCapacityEstimate(
                    modelId,
                    reader.GetInt32(5),
                    ParseDecimal(reader.GetString(2)),
                    ParseDecimal(reader.GetString(3)),
                    ParseDecimal(reader.GetString(4)),
                    source,
                    calibrationStart);
            }
            return latest.Values
                .OrderBy(item => item.ModelId, StringComparer.OrdinalIgnoreCase)
                .ToList();
        });
    }

    private static T Execute<T>(string operation, Func<SqliteConnection, T> action)
    {
        var path = MonitorSettingsDatabase.Path;
        var state = States.GetOrAdd(path, _ => new DatabaseState());
        lock (state.Sync)
        {
            try
            {
                // The root is captured once for initialization and the operation.
                // A known settings file must never be silently recreated.
                state.KnownDatabase |= File.Exists(path);
                using var connection = MonitorSettingsDatabase.OpenConnection(path, create: !state.KnownDatabase);
                state.KnownDatabase = true;
                EnsureInitialized(connection, state);
                return action(connection);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                state.Initialized = false;
                CacheOperationDiagnostics.Report(path, operation, ex);
                throw;
            }
        }
    }

    private static void EnsureInitialized(SqliteConnection connection, DatabaseState state)
    {
        if (state.Initialized) return;
        using var transaction = connection.BeginTransaction();
        using var exists = connection.CreateCommand();
        exists.Transaction = transaction;
        exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'quota_model_calibrations'";
        var tableExists = Convert.ToInt64(exists.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
        if (!tableExists && state.KnownTable)
            throw new InvalidDataException("已保存的校准表 quota_model_calibrations 缺失，未创建替代数据。");
        if (tableExists) state.KnownTable = true;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
                CREATE TABLE IF NOT EXISTS quota_model_calibrations (
                    plan_name TEXT NOT NULL,
                    model_id TEXT NOT NULL,
                    period_start TEXT NOT NULL,
                    period_end TEXT NOT NULL,
                    average_full_quota_cost TEXT NOT NULL,
                    minimum_full_quota_cost TEXT NOT NULL,
                    maximum_full_quota_cost TEXT NOT NULL,
                    sample_count INTEGER NOT NULL,
                    updated_at TEXT NOT NULL,
                    PRIMARY KEY (plan_name, model_id, period_start)
                );
                CREATE INDEX IF NOT EXISTS idx_quota_model_calibrations_period
                    ON quota_model_calibrations(plan_name, period_start);
                """;
        command.ExecuteNonQuery();
        transaction.Commit();
        state.KnownTable = true;
        state.Initialized = true;
    }

    private static string FormatDateTimeOffset(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    private static string FormatDecimal(decimal value) =>
        value.ToString(CultureInfo.InvariantCulture);

    private static decimal ParseDecimal(string value) =>
        decimal.Parse(value, CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseDateTimeOffset(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private sealed class DatabaseState
    {
        public object Sync { get; } = new();
        public bool KnownDatabase { get; set; }
        public bool KnownTable { get; set; }
        public bool Initialized { get; set; }
    }
}
