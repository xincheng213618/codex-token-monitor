namespace CodexTokenMonitor;

/// <summary>
/// Builds a stable historical prior and updates it from the current period as
/// one joint inverse-capacity regression. Inverse capacity makes a mixed band
/// linear: quotaDrop / 100 = sum(modelCost / modelCapacity).
/// </summary>
internal static class RobustQuotaModelCapacityEstimator
{
    private const decimal MinimumBandDropPercent = 2m;
    private const double HistoryHalfLifeDays = 56d;
    private const double MinimumLogScale = 0.08d;
    private const double HuberThreshold = 1.5d;
    private const int MaximumIterations = 8;

    public static IReadOnlyList<QuotaModelCapacityEstimate> BuildHistoricalPriors(
        IReadOnlyCollection<QuotaModelCapacityEstimate> history,
        DateTimeOffset currentPeriodStart)
    {
        return history
            .Where(item => item.AverageFullQuotaCost > 0m)
            .GroupBy(item => CodexModelCost.NormalizeModelId(item.ModelId), StringComparer.OrdinalIgnoreCase)
            .Select(group => BuildHistoricalPrior(group.Key, group.ToList(), currentPeriodStart))
            .Where(item => item is not null)
            .Cast<QuotaModelCapacityEstimate>()
            .OrderByDescending(item => item.BandCount)
            .ThenBy(item => item.ModelId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static IReadOnlyList<QuotaModelCapacityEstimate> RegressCurrentPeriod(
        QuotaCycleAnalysisResult result,
        IReadOnlyCollection<QuotaModelCapacityEstimate> baselineEstimates,
        CancellationToken cancellationToken = default)
    {
        var baselines = baselineEstimates
            .Where(item => item.AverageFullQuotaCost > 0m)
            .GroupBy(item => CodexModelCost.NormalizeModelId(item.ModelId), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        if (baselines.Count == 0)
        {
            return Array.Empty<QuotaModelCapacityEstimate>();
        }

        var modelIds = baselines.Keys.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToArray();
        var modelIndexes = modelIds
            .Select((model, index) => (model, index))
            .ToDictionary(item => item.model, item => item.index, StringComparer.OrdinalIgnoreCase);
        var observations = BuildObservations(result, baselines, modelIndexes);
        if (observations.Count == 0)
        {
            return Array.Empty<QuotaModelCapacityEstimate>();
        }

        var priorWeights = modelIds
            .Select(model => PriorWeight(baselines[model]))
            .ToArray();
        var state = Enumerable.Repeat(1d, modelIds.Length).ToArray();
        // Start from the historical/current-pure prior before the first solve.
        // Otherwise a high-leverage outlier can pull the first least-squares
        // solution to a bound and make good observations look like outliers.
        var robustWeights = observations
            .Select(observation => RobustWeight(observation, state))
            .ToArray();

        for (var iteration = 0; iteration < MaximumIterations; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var solved = Solve(observations, robustWeights, priorWeights);
            if (solved is null)
            {
                return Array.Empty<QuotaModelCapacityEstimate>();
            }

            var maximumChange = 0d;
            for (var index = 0; index < solved.Length; index++)
            {
                solved[index] = Math.Clamp(solved[index], 0.5d, 2d);
                maximumChange = Math.Max(maximumChange, Math.Abs(solved[index] - state[index]));
            }
            state = solved;

            for (var index = 0; index < observations.Count; index++)
            {
                robustWeights[index] = RobustWeight(observations[index], state);
            }

            if (maximumChange < 0.000001d)
            {
                break;
            }
        }

        var estimates = new List<QuotaModelCapacityEstimate>(modelIds.Length);
        for (var modelIndex = 0; modelIndex < modelIds.Length; modelIndex++)
        {
            var modelId = modelIds[modelIndex];
            var baseline = baselines[modelId];
            var capacity = baseline.AverageFullQuotaCost / (decimal)state[modelIndex];
            var contributing = observations.Count(item => item.Coefficients[modelIndex] > 0d);
            if (contributing == 0)
            {
                continue;
            }
            var mixed = observations.Count(item => item.IsMixed && item.Coefficients[modelIndex] > 0d);
            estimates.Add(new QuotaModelCapacityEstimate(
                modelId,
                contributing,
                capacity,
                Math.Min(baseline.MinimumFullQuotaCost, capacity),
                Math.Max(baseline.MaximumFullQuotaCost, capacity),
                QuotaModelCapacitySource.CurrentPeriodBlended,
                result.Period.PeriodStart,
                mixed,
                baseline.HistoricalPeriodCount,
                contributing));
        }

        return estimates
            .OrderByDescending(item => item.BandCount)
            .ThenBy(item => item.ModelId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static QuotaModelCapacityEstimate? BuildHistoricalPrior(
        string modelId,
        IReadOnlyCollection<QuotaModelCapacityEstimate> source,
        DateTimeOffset currentPeriodStart)
    {
        var samples = source
            .Where(item => item.AverageFullQuotaCost > 0m && item.AverageFullQuotaCost <= 100_000m)
            .GroupBy(item => item.CalibrationPeriodStart)
            .Select(group => group.OrderByDescending(item => item.BandCount).First())
            .Select(item =>
            {
                var ageDays = Math.Max(0d, (currentPeriodStart - item.CalibrationPeriodStart).TotalDays);
                var recency = Math.Pow(0.5d, ageDays / HistoryHalfLifeDays);
                var reliability = Math.Sqrt(Math.Clamp(item.BandCount, 1, 16));
                return new WeightedSample(
                    Math.Log((double)item.AverageFullQuotaCost),
                    recency * reliability,
                    item);
            })
            .Where(item => item.Weight > 0d && double.IsFinite(item.LogCapacity))
            .ToList();
        if (samples.Count == 0)
        {
            return null;
        }

        var center = WeightedQuantile(samples, 0.5d);
        var deviations = samples
            .Select(item => new WeightedValue(Math.Abs(item.LogCapacity - center), item.Weight))
            .ToList();
        var scale = Math.Max(MinimumLogScale, 1.4826d * WeightedQuantile(deviations, 0.5d));
        for (var iteration = 0; iteration < MaximumIterations; iteration++)
        {
            var numerator = 0d;
            var denominator = 0d;
            foreach (var sample in samples)
            {
                var distance = Math.Abs(sample.LogCapacity - center);
                var robustWeight = distance <= HuberThreshold * scale
                    ? 1d
                    : HuberThreshold * scale / distance;
                var weight = sample.Weight * robustWeight;
                numerator += weight * sample.LogCapacity;
                denominator += weight;
            }
            if (denominator <= 0d)
            {
                break;
            }
            var updated = numerator / denominator;
            if (Math.Abs(updated - center) < 0.000001d)
            {
                center = updated;
                break;
            }
            center = updated;
        }

        var withinPeriodSpreads = samples
            .Select(item =>
            {
                var minimum = Math.Max(0.000001d, (double)item.Source.MinimumFullQuotaCost);
                var maximum = Math.Max(minimum, (double)item.Source.MaximumFullQuotaCost);
                return new WeightedValue(Math.Abs(Math.Log(maximum / minimum)) / 2d, item.Weight);
            })
            .ToList();
        var priorScale = Math.Clamp(
            Math.Max(scale, WeightedQuantile(withinPeriodSpreads, 0.5d)),
            MinimumLogScale,
            Math.Log(4d));
        var average = (decimal)Math.Exp(center);
        var lower = (decimal)Math.Exp(center - priorScale);
        var upper = (decimal)Math.Exp(center + priorScale);
        return new QuotaModelCapacityEstimate(
            modelId,
            samples.Sum(item => Math.Clamp(item.Source.BandCount, 1, 16)),
            average,
            Math.Min(lower, average),
            Math.Max(upper, average),
            QuotaModelCapacitySource.PreviousPeriodApproved,
            samples.Max(item => item.Source.CalibrationPeriodStart),
            HistoricalPeriodCount: samples.Count);
    }

    private static List<RegressionObservation> BuildObservations(
        QuotaCycleAnalysisResult result,
        IReadOnlyDictionary<string, QuotaModelCapacityEstimate> baselines,
        IReadOnlyDictionary<string, int> modelIndexes)
    {
        var observations = new List<RegressionObservation>();
        foreach (var band in result.Bands)
        {
            if (band.QuotaDropPercent < MinimumBandDropPercent ||
                band.Models.Any(item => !item.IsPriced && item.QuotaSharePercent > 0m))
            {
                continue;
            }

            var costs = band.Models
                .Where(item => item.IsPriced && item.EquivalentCost > 0m)
                .GroupBy(item => CodexModelCost.NormalizeModelId(item.ModelId), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Sum(item => item.EquivalentCost), StringComparer.OrdinalIgnoreCase);
            if (costs.Count == 0 || costs.Keys.Any(model => !baselines.ContainsKey(model)))
            {
                continue;
            }

            var coefficients = new double[modelIndexes.Count];
            foreach (var item in costs)
            {
                coefficients[modelIndexes[item.Key]] =
                    (double)(item.Value / baselines[item.Key].AverageFullQuotaCost);
            }
            var target = (double)(band.QuotaDropPercent / 100m);
            var noise = Math.Max(0.004d, target * 0.10d);
            var bandWeight = Math.Clamp((double)(band.QuotaDropPercent / 5m), 0.5d, 4d);
            observations.Add(new RegressionObservation(coefficients, target, noise, bandWeight, costs.Count > 1));
        }
        return observations;
    }

    private static double PriorWeight(QuotaModelCapacityEstimate baseline)
    {
        double sigma;
        if (baseline.HistoricalPeriodCount > 0)
        {
            var lower = Math.Max(0.000001d, (double)baseline.MinimumFullQuotaCost);
            var upper = Math.Max(lower, (double)baseline.MaximumFullQuotaCost);
            var range = Math.Abs(Math.Log(upper / lower)) / 2d;
            sigma = Math.Clamp(range, 0.12d, 0.45d);
            sigma /= Math.Sqrt(Math.Min(4d, baseline.HistoricalPeriodCount));
            sigma = Math.Max(0.10d, sigma);
        }
        else
        {
            sigma = baseline.Source == QuotaModelCapacitySource.CurrentPeriodInferred ? 0.50d : 0.30d;
        }
        return 1d / (sigma * sigma);
    }

    private static double[]? Solve(
        IReadOnlyList<RegressionObservation> observations,
        IReadOnlyList<double> robustWeights,
        IReadOnlyList<double> priorWeights)
    {
        var size = priorWeights.Count;
        var matrix = new double[size, size];
        var rhs = new double[size];
        for (var model = 0; model < size; model++)
        {
            matrix[model, model] = priorWeights[model];
            rhs[model] = priorWeights[model];
        }

        for (var observationIndex = 0; observationIndex < observations.Count; observationIndex++)
        {
            var observation = observations[observationIndex];
            var weight = observation.BandWeight * robustWeights[observationIndex] /
                         (observation.Noise * observation.Noise);
            for (var row = 0; row < size; row++)
            {
                rhs[row] += weight * observation.Coefficients[row] * observation.Target;
                for (var column = 0; column < size; column++)
                {
                    matrix[row, column] += weight * observation.Coefficients[row] * observation.Coefficients[column];
                }
            }
        }

        return SolveLinearSystem(matrix, rhs);
    }

    private static double[]? SolveLinearSystem(double[,] matrix, double[] rhs)
    {
        var size = rhs.Length;
        for (var pivot = 0; pivot < size; pivot++)
        {
            var best = pivot;
            for (var row = pivot + 1; row < size; row++)
            {
                if (Math.Abs(matrix[row, pivot]) > Math.Abs(matrix[best, pivot]))
                {
                    best = row;
                }
            }
            if (Math.Abs(matrix[best, pivot]) < 1e-12d)
            {
                return null;
            }
            if (best != pivot)
            {
                for (var column = pivot; column < size; column++)
                {
                    (matrix[pivot, column], matrix[best, column]) = (matrix[best, column], matrix[pivot, column]);
                }
                (rhs[pivot], rhs[best]) = (rhs[best], rhs[pivot]);
            }

            var divisor = matrix[pivot, pivot];
            for (var column = pivot; column < size; column++)
            {
                matrix[pivot, column] /= divisor;
            }
            rhs[pivot] /= divisor;
            for (var row = 0; row < size; row++)
            {
                if (row == pivot) continue;
                var factor = matrix[row, pivot];
                if (Math.Abs(factor) < 1e-18d) continue;
                for (var column = pivot; column < size; column++)
                {
                    matrix[row, column] -= factor * matrix[pivot, column];
                }
                rhs[row] -= factor * rhs[pivot];
            }
        }
        return rhs.All(double.IsFinite) ? rhs : null;
    }

    private static double Dot(IReadOnlyList<double> left, IReadOnlyList<double> right)
    {
        var result = 0d;
        for (var index = 0; index < left.Count; index++)
        {
            result += left[index] * right[index];
        }
        return result;
    }

    private static double RobustWeight(RegressionObservation observation, IReadOnlyList<double> state)
    {
        var predicted = Dot(observation.Coefficients, state);
        var standardizedResidual = Math.Abs(observation.Target - predicted) / observation.Noise;
        return standardizedResidual <= HuberThreshold
            ? 1d
            : HuberThreshold / standardizedResidual;
    }

    private static double WeightedQuantile(IReadOnlyCollection<WeightedSample> samples, double quantile) =>
        WeightedQuantile(samples.Select(item => new WeightedValue(item.LogCapacity, item.Weight)).ToList(), quantile);

    private static double WeightedQuantile(IReadOnlyCollection<WeightedValue> samples, double quantile)
    {
        var ordered = samples.OrderBy(item => item.Value).ToList();
        var total = ordered.Sum(item => item.Weight);
        var threshold = total * Math.Clamp(quantile, 0d, 1d);
        var cumulative = 0d;
        foreach (var item in ordered)
        {
            cumulative += item.Weight;
            if (cumulative >= threshold)
            {
                return item.Value;
            }
        }
        return ordered[^1].Value;
    }

    private sealed record WeightedSample(double LogCapacity, double Weight, QuotaModelCapacityEstimate Source);
    private sealed record WeightedValue(double Value, double Weight);
    private sealed record RegressionObservation(
        double[] Coefficients,
        double Target,
        double Noise,
        double BandWeight,
        bool IsMixed);
}
