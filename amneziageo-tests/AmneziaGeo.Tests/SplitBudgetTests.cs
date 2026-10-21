using AmneziaGeo.Routing;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// A list that carries only what it names stays on the route table up to a fixed size and goes to the relay beyond.
/// </summary>
public sealed class SplitBudgetTests
{
    [Theory]
    [InlineData(10_000, 0, true)]
    [InlineData(10_001, 0, false)]
    [InlineData(9_992, 1, true)]
    [InlineData(9_993, 1, false)]
    [InlineData(0, 1_250, true)]
    [InlineData(0, 1_251, false)]
    public void TheLimit_CountsEveryNameAsEightAddresses(int routes, int names, bool fits)
    {
        Assert.Equal(fits, SplitBudget.Fits(routes, names));
    }

    [Fact]
    public void EveryDatagramOnTheTunnel_TakesTheRelayWhateverTheSize()
    {
        Assert.True(SplitBudget.Relayed(3, 0, allUdp: true));
        Assert.False(SplitBudget.Relayed(3, 0, allUdp: false));
        Assert.True(SplitBudget.Relayed(SplitBudget.Limit + 1, 0, allUdp: false));
    }
}
