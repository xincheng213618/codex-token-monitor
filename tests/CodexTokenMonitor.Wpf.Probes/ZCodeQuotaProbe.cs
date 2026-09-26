using CodexTokenMonitor;

namespace CodexTokenMonitor.Wpf.Probes;

/// <summary>
/// Headless diagnostic for the ZCode quota pipeline: performs one live read
/// with the machine's real credentials and reports the classified outcome.
/// Unlike the UI suites it does not open windows or seed offline data, so it
/// can be used to tell "not signed in" apart from rate limits and outages.
/// </summary>
internal static class ZCodeQuotaProbe
{
    internal static void Run(string output)
    {
        var checks = new List<object>();
        try
        {
            var first = ZCodeQuotaReader.Shared.ReadCurrentResult();
            if (first.Snapshot is { } snapshot)
            {
                checks.Add(new
                {
                    check = "live-read-snapshot",
                    passed = snapshot.Balances.Count > 0,
                    planId = snapshot.PlanId,
                    planName = snapshot.PlanName,
                    planStatus = snapshot.PlanStatus,
                    balanceCount = snapshot.Balances.Count,
                    balances = snapshot.Balances.Select(item => new
                    {
                        item.ModelName,
                        item.ModelId,
                        item.TotalUnits,
                        item.UsedUnits,
                        item.RemainingUnits,
                        UsedPercent = item.UsedPercent,
                        ExpiresAtLocal = item.ExpiresAtLocal?.ToString("yyyy-MM-dd HH:mm zzz")
                    }).ToArray(),
                    snapshotLocal = snapshot.SnapshotLocal.ToString("yyyy-MM-dd HH:mm:ss zzz")
                });
                checks.Add(new
                {
                    check = "primary-balance-selected",
                    passed = snapshot.PrimaryBalance is not null &&
                             snapshot.PrimaryBalance.RemainingUnits >= 0 &&
                             snapshot.PrimaryBalance.TotalUnits >= snapshot.PrimaryBalance.UsedUnits,
                    modelName = snapshot.PrimaryBalance?.ModelName,
                    remainingUnits = snapshot.PrimaryBalance?.RemainingUnits
                });
            }
            else
            {
                checks.Add(new
                {
                    check = "live-read-failure-classified",
                    passed = first.Failure is not null &&
                             first.Failure.Kind != ZCodeQuotaFailureKind.Unknown,
                    kind = first.Failure?.Kind.ToString(),
                    statusCode = first.Failure?.StatusCode,
                    retryAfterSeconds = first.Failure?.RetryAfter?.TotalSeconds,
                    message = first.Failure?.Message
                });
            }

            var second = ZCodeQuotaReader.Shared.ReadCurrentResult();
            checks.Add(new
            {
                check = "second-read-served-from-cache",
                passed = ReferenceEquals(first.Snapshot, second.Snapshot) &&
                         ReferenceEquals(first.Failure, second.Failure)
            });

            ProbeReport.Write(output, "zcode", checks.All(item => (bool)item.GetType().GetProperty("passed")!.GetValue(item)!), null, checks);
        }
        catch (Exception ex)
        {
            ProbeReport.Write(output, "zcode", false, ex.ToString(), checks);
            throw;
        }
    }
}
