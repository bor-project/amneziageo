using AmneziaGeo.Routing;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// A list that carries only what it names stays on the route table up to the threshold of the settings and goes to
/// the relay beyond; the threshold stands at 11000 until the settings name another.
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

    [Fact]
    public void EveryDatagramOnTheTunnel_TakesTheRelayWhateverTheSize()
    {
        Assert.True(SplitBudget.Relayed(3, 0, true, SplitBudget.DefaultLimit));
        Assert.False(SplitBudget.Relayed(3, 0, false, SplitBudget.DefaultLimit));
        Assert.True(SplitBudget.Relayed(SplitBudget.DefaultLimit + 1, 0, false, SplitBudget.DefaultLimit));
        Assert.False(SplitBudget.Relayed(SplitBudget.DefaultLimit + 1, 0, false, 16_000));
    }
}
