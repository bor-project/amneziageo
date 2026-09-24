using System.Net;
using AmneziaGeo.Windows.App;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// A VPN beside this one pins its server with a host route through the physical adapter; the all-UDP rule leaves
/// that address on it, so the other VPN does not end up inside this tunnel.
/// </summary>
public sealed class OtherVpnRouteTests
{
    private const uint Physical = 14;
    private const uint Tunnel = 33;

    private static readonly IPAddress Server = IPAddress.Parse("195.54.192.102");

    [Fact]
    public void HostRouteOfAnotherProgramOnThePhysicalAdapterIsForeign()
    {
        Assert.True(RouteManager.ForeignHostRoute(false, true, 32, Physical, Tunnel, false));
        Assert.True(RouteManager.ForeignHostRoute(true, true, 128, Physical, Tunnel, false));
    }

    [Fact]
    public void HostRouteIsForeignWhileTheTunnelAdapterIsNotFoundYet()
    {
        Assert.True(RouteManager.ForeignHostRoute(false, true, 32, Physical, null, false));
    }

    [Fact]
    public void RouteOnTheTunnelOrInstalledByThisAgentIsNotForeign()
    {
        Assert.False(RouteManager.ForeignHostRoute(false, true, 32, Tunnel, Tunnel, false));
        Assert.False(RouteManager.ForeignHostRoute(false, true, 32, Physical, Tunnel, true));
    }

    [Fact]
    public void WiderRouteOrRouteToAnotherAddressIsNotForeign()
    {
        Assert.False(RouteManager.ForeignHostRoute(false, true, 24, Physical, Tunnel, false));
        Assert.False(RouteManager.ForeignHostRoute(false, true, 0, Physical, Tunnel, false));
        Assert.False(RouteManager.ForeignHostRoute(true, true, 32, Physical, Tunnel, false));
        Assert.False(RouteManager.ForeignHostRoute(false, false, 32, Physical, Tunnel, false));
    }

    [Fact]
    public void AllUdpLeavesTheServerOfAnotherVpnOnItsRoute()
    {
        var tracker = Tracker(true, address => address.Equals(Server));

        Assert.True(tracker.LeftToItsOwnRoute(Server));
        Assert.False(tracker.LeftToItsOwnRoute(IPAddress.Parse("149.154.167.99")));
    }

    [Fact]
    public void RulesByAppKeepTheirDestinations()
    {
        Assert.False(Tracker(false, _ => true).LeftToItsOwnRoute(Server));
        Assert.False(Tracker(true, null).LeftToItsOwnRoute(Server));
    }

    private static NetworkFlowTracker Tracker(bool allUdp, Func<IPAddress, bool>? heldElsewhere)
    {
        return new NetworkFlowTracker(null, null, allUdp, false, null, NullLogger<NetworkFlowTracker>.Instance, heldElsewhere: heldElsewhere);
    }
}
