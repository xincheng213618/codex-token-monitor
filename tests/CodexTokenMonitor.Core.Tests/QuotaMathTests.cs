using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class QuotaMathTests
{
    [Fact]
    public void EstimateLimit_ReturnsNullInsteadOfOverflowing()
    {
        var result = QuotaMath.EstimateLimit(
            decimal.MaxValue,
            0.0000000000000000000000000001m);

        Assert.Null(result);
    }

    [Fact]
    public void EstimateLimit_ReturnsExpectedValueForNormalPercentage()
    {
        var result = QuotaMath.EstimateLimit(25m, 25m);

        Assert.Equal(100m, result);
    }

    [Fact]
    public void EstimateTokenLimit_SaturatesAtLongMaxValue()
    {
        var result = QuotaMath.EstimateTokenLimit(
            long.MaxValue,
            0.000000000000000001m);

        Assert.Equal(long.MaxValue, result);
    }

    [Fact]
    public void EstimateTokenLimit_UsesNormalRatio()
    {
        var result = QuotaMath.EstimateTokenLimit(100, 25m);

        Assert.Equal(400, result);
    }

    [Fact]
    public void EstimateLimit_RejectsInvalidPercentage()
    {
        Assert.Null(QuotaMath.EstimateLimit(10m, -1m));
        Assert.Null(QuotaMath.EstimateLimit(10m, 101m));
        Assert.Null(QuotaMath.EstimateLimit(10m, 0m));
    }

    [Fact]
    public void EstimateLimit_RejectsInvalidCost()
    {
        Assert.Null(QuotaMath.EstimateLimit(-1m, 25m));
    }
}
