using AmneziaGeo.Windows.App;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The address range of the per-application adapter: every tunnel of a fleet raises one of its own, so two of
/// them must never be handed the same network.
/// </summary>
public sealed class AppGatewayRangeTests
{
    [Fact]
    public void NothingHeld_TakesTheFirstRange()
    {
        Assert.Equal("172.31.73.1/24", AppGateway.Range([]));
    }

    [Fact]
    public void HeldRange_IsSteppedOver()
    {
        Assert.Equal("172.31.75.1/24", AppGateway.Range([72, 73, 74]));
    }

    [Fact]
    public void GapBelow_IsTakenBeforeTheRest()
    {
        Assert.Equal("172.31.74.1/24", AppGateway.Range([73, 75, 76]));
    }

    [Fact]
    public void EveryRangeHeld_LeavesNone()
    {
        Assert.Null(AppGateway.Range([.. Enumerable.Range(73, 27)]));
    }
}
