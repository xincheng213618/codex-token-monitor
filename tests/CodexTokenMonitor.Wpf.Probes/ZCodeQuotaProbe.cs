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
                    passed = snapshot.Plans.Count > 0,
                    planCount = snapshot.Plans.Count,
                    plans = snapshot.Plans.Select(plan => new
                    {
                        plan.PlanId,
                        plan.UserPlanId,
                        plan.Name,
                        plan.Status,
                        StartsAtLocal = plan.StartsAtLocal?.ToString("yyyy-MM-dd HH:mm zzz"),
                        EndsAtLocal = plan.EndsAtLocal?.ToString("yyyy-MM-dd HH:mm zzz"),
                        balances = plan.Balances.Select(item => new
                        {
                            item.ModelName,
                            item.ModelId,
                            item.TotalUnits,
                            item.UsedUnits,
                            item.RemainingUnits,
                            UsedPercent = item.UsedPercent,
                            ExpiresAtLocal = item.ExpiresAtLocal?.ToString("yyyy-MM-dd HH:mm zzz")
                        }).ToArray()
                    }).ToArray(),
                    snapshotLocal = snapshot.SnapshotLocal.ToString("yyyy-MM-dd HH:mm:ss zzz")
                });
                var defaultPlan = snapshot.DefaultPlan;
                checks.Add(new
                {
                    check = "default-plan-primary-balance-selected",
                    passed = defaultPlan.PrimaryBalance is not null &&
                             defaultPlan.PrimaryBalance.RemainingUnits >= 0 &&
                             defaultPlan.PrimaryBalance.TotalUnits >= defaultPlan.PrimaryBalance.UsedUnits,
                    planName = defaultPlan.Name,
                    modelName = defaultPlan.PrimaryBalance?.ModelName,
                    remainingUnits = defaultPlan.PrimaryBalance?.RemainingUnits
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
