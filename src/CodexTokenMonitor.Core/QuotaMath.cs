namespace CodexTokenMonitor;

internal static class QuotaMath
{
    public static decimal? EstimateLimit(decimal usedCost, decimal usedPercent)
    {
        if (usedCost < 0m || usedPercent <= 0m || usedPercent > 100m)
        {
            return null;
        }

        var ratio = usedPercent / 100m;
        if (ratio <= 0m)
        {
            return null;
        }

        try
        {
            return usedCost / ratio;
        }
        catch (Exception ex) when (ex is OverflowException or DivideByZeroException)
        {
            // A corrupted or extremely small percentage should not bring down
            // the estimate view after the token cost has already been read.
            return null;
        }
    }

    public static long? EstimateTokenLimit(long totalTokens, decimal usedPercent)
    {
        if (totalTokens < 0 || usedPercent <= 0m || usedPercent > 100m)
        {
            return null;
        }

        if (totalTokens == 0)
        {
            return 0;
        }

        var ratio = usedPercent / 100m;
        var minimumRatioForLongMax = (decimal)totalTokens / long.MaxValue;
        if (ratio <= minimumRatioForLongMax)
        {
            return long.MaxValue;
        }

        try
        {
            var estimate = (decimal)totalTokens / ratio;
            return estimate >= long.MaxValue
                ? long.MaxValue
                : (long)Math.Round(estimate, MidpointRounding.AwayFromZero);
        }
        catch (OverflowException)
        {
            return long.MaxValue;
        }
    }
}
