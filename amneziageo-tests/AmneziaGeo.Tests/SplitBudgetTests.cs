using AmneziaGeo.Routing;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// A list stays on the route table up to the threshold of the settings, counting every name as eight addresses; the
/// threshold stands at 11000 until the settings name another.
/// </summary>
public sealed class SplitBudgetTests
{
    [Fact]
    public void TheThreshold_StandsAtElevenThousand()
    {
        Assert.Equal(11_000, SplitBudget.DefaultLimit);
    }

    [Theory]
    [InlineData(11_000, 0, 11_000, true)]
    [InlineData(11_001, 0, 11_000, false)]
    [InlineData(10_992, 1, 11_000, true)]
    [InlineData(10_993, 1, 11_000, false)]
    [InlineData(0, 2_000, 16_000, true)]
    [InlineData(0, 2_001, 16_000, false)]
    public void TheThreshold_CountsEveryNameAsEightAddresses(int routes, int names, int limit, bool fits)
    {
        Assert.Equal(fits, SplitBudget.Fits(routes, names, limit));
    }

    [Fact]
    public void TheWeight_IsTheRangesAndEightAddressesAName()
    {
        Assert.Equal(1_048, SplitBudget.Weight(1_024, 3));
    }
}
