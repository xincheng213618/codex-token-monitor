using System.Globalization;
using Microsoft.Data.Sqlite;

namespace CodexTokenMonitor;

internal enum QuotaModelCapacitySource
{
    CurrentPeriodApproved,
    CurrentPeriodBlended,
    PreviousPeriodApproved,
    CurrentPeriodInferred
}

internal sealed record QuotaModelCapacityEstimate(
    string ModelId,
    int BandCount,
    decimal AverageFullQuotaCost,
    decimal MinimumFullQuotaCost,
    decimal MaximumFullQuotaCost,
    QuotaModelCapacitySource Source,
    DateTimeOffset CalibrationPeriodStart,
    int MixedBandCount = 0);

internal sealed record QuotaModelCapacityReport(
    string PlanName,
    IReadOnlyList<QuotaModelCapacityEstimate> Estimates);

internal static class QuotaModelCapacityEstimator
{
    internal const decimal PureModelSharePercent = 99m;
    private const decimal MinimumBandDropPercent = 2m;
    private const decimal MinimumResidualDropPercent = 0.05m;

    public static IReadOnlyList<QuotaModelCapacityEstimate> EstimatePureModels(
        QuotaCycleAnalysisResult result)
    {
        var visibleModels = result.Models
            .Where(item => Math.Round(item.QuotaSharePercent, 1, MidpointRounding.AwayFromZero) > 0m)
            .Select(item => CodexModelCost.NormalizeModelId(item.ModelId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var samples = new Dictionary<string, List<decimal>>(StringComparer.OrdinalIgnoreCase);
        foreach (var band in result.Bands)
        {
            if (band.QuotaDropPercent < MinimumBandDropPercent ||
                band.EstimatedFullQuotaCost is not { } estimate || estimate <= 0m)
            {
                continue;
            }

            var dominant = band.Models.FirstOrDefault();
            if (dominant is null ||
                Math.Round(dominant.QuotaSharePercent, 0, MidpointRounding.AwayFromZero) < PureModelSharePercent)
            {
                continue;
            }

            var modelId = CodexModelCost.NormalizeModelId(dominant.ModelId);
            if (result.Models.Count > 0 && !visibleModels.Contains(modelId))
            {
                continue;
            }
            if (!samples.TryGetValue(modelId, out var values))
            {
                samples[modelId] = values = new List<decimal>();
            }
            values.Add(estimate);
        }

        return samples
            .Select(item => BuildEstimate(
                item.Key,
                item.Value,
                QuotaModelCapacitySource.CurrentPeriodApproved,
                result.Period.PeriodStart))
            .OrderByDescending(item => item.BandCount)
            .ThenBy(item => item.ModelId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static IReadOnlyList<QuotaModelCapacityEstimate> InferMixedModels(
        QuotaCycleAnalysisResult result,
        IReadOnlyCollection<QuotaModelCapacityEstimate> approvedEstimates)
    {
        var approved = approvedEstimates
            .Where(item => item.Source != QuotaModelCapacitySource.CurrentPeriodInferred &&
                           item.AverageFullQuotaCost > 0m)
            .GroupBy(item => CodexModelCost.NormalizeModelId(item.ModelId), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var samples = new Dictionary<string, List<decimal>>(StringComparer.OrdinalIgnoreCase);

        foreach (var band in result.Bands)
        {
            if (band.QuotaDropPercent < MinimumBandDropPercent)
            {
                continue;
            }

            var modelCosts = band.Models
                .Where(item => item.IsPriced && item.EquivalentCost > 0m)
                .GroupBy(item => CodexModelCost.NormalizeModelId(item.ModelId), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Sum(item => item.EquivalentCost), StringComparer.OrdinalIgnoreCase);
            var unknownModels = modelCosts.Keys.Where(model => !approved.ContainsKey(model)).ToList();
            if (unknownModels.Count != 1)
            {
                continue;
            }

            var knownDrop = modelCosts
                .Where(item => approved.TryGetValue(item.Key, out _))
                .Sum(item => item.Value / approved[item.Key].AverageFullQuotaCost * 100m);
            var residualDrop = band.QuotaDropPercent - knownDrop;
            if (residualDrop <= MinimumResidualDropPercent)
            {
                continue;
            }

            var unknownModel = unknownModels[0];
            var estimate = modelCosts[unknownModel] / residualDrop * 100m;
            if (estimate <= 0m || estimate > 100_000m)
            {
                continue;
            }

            if (!samples.TryGetValue(unknownModel, out var values))
            {
                samples[unknownModel] = values = new List<decimal>();
            }
            values.Add(estimate);
        }

        return samples
            .Select(item => BuildEstimate(
                item.Key,
                item.Value,
                QuotaModelCapacitySource.CurrentPeriodInferred,
                result.Period.PeriodStart))
            .OrderByDescending(item => item.BandCount)
            .ThenBy(item => item.ModelId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static IReadOnlyList<QuotaModelCapacityEstimate> BlendMixedModels(
        QuotaCycleAnalysisResult result,
        IReadOnlyCollection<QuotaModelCapacityEstimate> baselineEstimates,
        IReadOnlyCollection<string> currentPureModels)
    {
        var baselines = baselineEstimates
            .Where(item => item.AverageFullQuotaCost > 0m)
            .GroupBy(item => CodexModelCost.NormalizeModelId(item.ModelId), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var targets = currentPureModels
            .Select(CodexModelCost.NormalizeModelId)
            .Where(baselines.ContainsKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var visibleModels = result.Models
            .Where(item => Math.Round(item.QuotaSharePercent, 1, MidpointRounding.AwayFromZero) > 0m)
            .Select(item => CodexModelCost.NormalizeModelId(item.ModelId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var contributions = targets.ToDictionary(
            model => model,
            _ => new List<MixedContribution>(),
            StringComparer.OrdinalIgnoreCase);

        foreach (var band in result.Bands)
        {
            if (band.QuotaDropPercent < MinimumBandDropPercent ||
                band.EstimatedFullQuotaCost is not { } observedCapacity || observedCapacity <= 0m ||
                band.Models.FirstOrDefault() is not { } dominant ||
                Math.Round(dominant.QuotaSharePercent, 0, MidpointRounding.AwayFromZero) >= PureModelSharePercent)
            {
                continue;
            }

            var modelCosts = band.Models
                .Where(item => item.IsPriced && item.EquivalentCost > 0m)
                .Select(item => new
                {
                    ModelId = CodexModelCost.NormalizeModelId(item.ModelId),
                    item.EquivalentCost
                })
                .Where(item => visibleModels.Count == 0 || visibleModels.Contains(item.ModelId))
                .GroupBy(item => item.ModelId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Sum(item => item.EquivalentCost), StringComparer.OrdinalIgnoreCase);
            if (modelCosts.Count < 2 || modelCosts.Keys.Any(model => !baselines.ContainsKey(model)))
            {
                continue;
            }

            var totalCost = modelCosts.Values.Sum();
            var inverseCapacity = modelCosts.Sum(item =>
                item.Value / totalCost / baselines[item.Key].AverageFullQuotaCost);
            if (totalCost <= 0m || inverseCapacity <= 0m)
            {
                continue;
            }

            var predictedCapacity = 1m / inverseCapacity;
            var correction = observedCapacity / predictedCapacity;
            if (correction < 0.5m || correction > 2m)
            {
                continue;
            }

            foreach (var item in modelCosts.Where(item => targets.Contains(item.Key)))
            {
                contributions[item.Key].Add(new MixedContribution(
                    baselines[item.Key].AverageFullQuotaCost * correction,
                    item.Value / totalCost));
            }
        }

        return contributions
            .Where(item => item.Value.Count > 0)
            .Select(item =>
            {
                var baseline = baselines[item.Key];
                var mixedWeight = item.Value.Sum(value => value.Weight);
                var totalWeight = baseline.BandCount + mixedWeight;
                var average = totalWeight <= 0m
                    ? baseline.AverageFullQuotaCost
                    : (baseline.AverageFullQuotaCost * baseline.BandCount +
                       item.Value.Sum(value => value.Capacity * value.Weight)) / totalWeight;
                return new QuotaModelCapacityEstimate(
                    item.Key,
                    baseline.BandCount + item.Value.Count,
                    average,
                    Math.Min(baseline.MinimumFullQuotaCost, item.Value.Min(value => value.Capacity)),
                    Math.Max(baseline.MaximumFullQuotaCost, item.Value.Max(value => value.Capacity)),
                    QuotaModelCapacitySource.CurrentPeriodBlended,
                    result.Period.PeriodStart,
                    item.Value.Count);
            })
            .OrderByDescending(item => item.BandCount)
            .ThenBy(item => item.ModelId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static QuotaModelCapacityEstimate BuildEstimate(
        string modelId,
        IReadOnlyCollection<decimal> samples,
        QuotaModelCapacitySource source,
        DateTimeOffset periodStart) => new(
        modelId,
        samples.Count,
        samples.Average(),
        samples.Min(),
        samples.Max(),
        source,
        periodStart);

    private sealed record MixedContribution(decimal Capacity, decimal Weight);
}

internal static class QuotaModelCapacityCalibrationService
{
    public static QuotaModelCapacityReport Build(
        CodexQuotaCycle period,
        QuotaCycleAnalysisResult result,
        CodexQuotaCycle? previousPeriod,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var planName = ResolvePlanName(period);
        var pureEstimates = QuotaModelCapacityEstimator.EstimatePureModels(result);
        QuotaModelCapacityCalibrationStore.Upsert(planName, period, pureEstimates);

        var currentModels = result.Models
            .Where(item => item.IsPriced && item.EquivalentCost > 0m &&
                           Math.Round(item.QuotaSharePercent, 1, MidpointRounding.AwayFromZero) > 0m)
            .Select(item => CodexModelCost.NormalizeModelId(item.ModelId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var currentApproved = QuotaModelCapacityCalibrationStore
            .LoadExact(planName, period.PeriodStart, QuotaModelCapacitySource.CurrentPeriodApproved)
            .Where(item => currentModels.Contains(item.ModelId, StringComparer.OrdinalIgnoreCase))
            .ToDictionary(item => item.ModelId, StringComparer.OrdinalIgnoreCase);

        var missingModels = currentModels
            .Where(model => !currentApproved.ContainsKey(model))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var previousApproved = new Dictionary<string, QuotaModelCapacityEstimate>(StringComparer.OrdinalIgnoreCase);
        if (missingModels.Count > 0 && previousPeriod is not null &&
            previousPeriod.PeriodStart < period.PeriodStart &&
            string.Equals(ResolvePlanName(previousPeriod), planName, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var item in QuotaModelCapacityCalibrationStore.LoadExact(
                         planName,
                         previousPeriod.PeriodStart,
                         QuotaModelCapacitySource.PreviousPeriodApproved))
            {
                if (missingModels.Contains(item.ModelId))
                {
                    previousApproved[item.ModelId] = item;
                }
            }

            if (missingModels.Any(model => !previousApproved.ContainsKey(model)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var previousResult = QuotaCycleAnalysisCalculator.Build(previousPeriod, cancellationToken);
                var previousPure = QuotaModelCapacityEstimator.EstimatePureModels(previousResult);
                QuotaModelCapacityCalibrationStore.Upsert(planName, previousPeriod, previousPure);
                foreach (var item in QuotaModelCapacityCalibrationStore.LoadExact(
                             planName,
                             previousPeriod.PeriodStart,
                             QuotaModelCapacitySource.PreviousPeriodApproved))
                {
                    if (missingModels.Contains(item.ModelId))
                    {
                        previousApproved[item.ModelId] = item;
                    }
                }
            }
        }

        var approved = currentModels
            .Select(model => currentApproved.GetValueOrDefault(model) ?? previousApproved.GetValueOrDefault(model))
            .Where(item => item is not null)
            .Cast<QuotaModelCapacityEstimate>()
            .ToList();
        var inferred = QuotaModelCapacityEstimator.InferMixedModels(result, approved)
            .ToDictionary(item => item.ModelId, StringComparer.OrdinalIgnoreCase);
        var baselines = approved
            .Concat(inferred.Values)
            .ToList();
        var blended = QuotaModelCapacityEstimator.BlendMixedModels(
                result,
                baselines,
                pureEstimates.Select(item => item.ModelId).ToList())
            .ToDictionary(item => item.ModelId, StringComparer.OrdinalIgnoreCase);
        if (blended.Count > 0)
        {
            QuotaModelCapacityCalibrationStore.Upsert(planName, period, blended.Values.ToList());
        }
        var estimates = currentModels
            .Select(model => blended.GetValueOrDefault(model) ??
                             currentApproved.GetValueOrDefault(model) ??
                             previousApproved.GetValueOrDefault(model) ??
                             inferred.GetValueOrDefault(model))
            .Where(item => item is not null)
            .Cast<QuotaModelCapacityEstimate>()
            .ToList();
        return new QuotaModelCapacityReport(planName, estimates);
    }

    private static string ResolvePlanName(CodexQuotaCycle period)
    {
        var summary = SubscriptionPlanStore.Summarize(period.PeriodStart, period.PeriodEnd);
        if (!summary.HasRecords)
        {
            return "未设置套餐";
        }

        return summary.Records
            .GroupBy(item => item.PlanName, StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                Name = group.Key,
                Ticks = group.Sum(item => Math.Max(0L,
                    (Min(period.PeriodEnd, item.EndLocal) - Max(period.PeriodStart, item.StartLocal)).Ticks))
            })
            .OrderByDescending(item => item.Ticks)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .First().Name;
    }

    private static DateTimeOffset Max(DateTimeOffset first, DateTimeOffset second) =>
        first >= second ? first : second;

    private static DateTimeOffset Min(DateTimeOffset first, DateTimeOffset second) =>
        first <= second ? first : second;
}

internal static class QuotaModelCapacityCalibrationStore
{
    private static readonly object SyncRoot = new();
    private static string? initializedPath;

    public static void Upsert(
        string planName,
        CodexQuotaCycle period,
        IReadOnlyCollection<QuotaModelCapacityEstimate> estimates)
    {
        if (estimates.Count == 0)
        {
            return;
        }

        EnsureInitialized();
        lock (SyncRoot)
        {
            using var connection = OpenConnection();
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
        }
    }

    public static IReadOnlyList<QuotaModelCapacityEstimate> LoadExact(
        string planName,
        DateTimeOffset periodStart,
        QuotaModelCapacitySource source)
    {
        EnsureInitialized();
        lock (SyncRoot)
        {
            using var connection = OpenConnection();
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
        }
    }

    private static void EnsureInitialized()
    {
        lock (SyncRoot)
        {
            if (initializedPath == MonitorSettingsDatabase.Path)
            {
                return;
            }

            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
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
            initializedPath = MonitorSettingsDatabase.Path;
        }
    }

    private static SqliteConnection OpenConnection()
    {
        var path = MonitorSettingsDatabase.Path;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=5000;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static string FormatDateTimeOffset(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    private static string FormatDecimal(decimal value) =>
        value.ToString(CultureInfo.InvariantCulture);

    private static decimal ParseDecimal(string value) =>
        decimal.Parse(value, CultureInfo.InvariantCulture);
}
