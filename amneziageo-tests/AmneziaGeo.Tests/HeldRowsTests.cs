using System.Net;
using AmneziaGeo.Routing;
using AmneziaGeo.Windows.App;
using Xunit;
using Row = AmneziaGeo.Ipc.LiveSession;

namespace AmneziaGeo.Tests;

/// <summary>
/// The cache report of the support archive names the path an address really takes: a route installed for all-UDP
/// or for a matched app is a tunnel one, and the server stands past the tunnel.
/// </summary>
public sealed class HeldRowsTests
{
    [Fact]
    public void AnAddressAllUdpRouted_ReadsTunnelByUdp()
    {
        var row = HeldRows.Of(Held(), split: true, name: string.Empty, named: false, datagrams: true);

        Assert.Equal(Row.PathTunnel, row.Route);
        Assert.Equal(Row.ReasonUdp, row.Reason);
        Assert.Equal(Row.Undecided, row.Verdict);
    }

    [Fact]
    public void AnAddressTheCachePermittedPastTheTunnel_StillReadsTunnelOnceRouted()
    {
        var row = HeldRows.Of(Held(plan: RoutePlan.Permit), split: true, name: string.Empty, named: false, datagrams: true);

        Assert.Equal((Row.PathTunnel, Row.ReasonUdp), (row.Route, row.Reason));
    }

    [Fact]
    public void AnAddressAMatchedAppRouted_ReadsTunnelByApp()
    {
        var row = HeldRows.Of(Held(), split: true, name: string.Empty, named: false, datagrams: false);

        Assert.Equal(Row.PathTunnel, row.Route);
        Assert.Equal(Row.ReasonApp, row.Reason);
    }

    [Fact]
    public void AnAddressNothingRouted_FollowsTheMode()
    {
        var split = HeldRows.Of(Held(), split: true, name: string.Empty, named: false, datagrams: null);
        var full = HeldRows.Of(Held(), split: false, name: string.Empty, named: false, datagrams: null);

        Assert.Equal((Row.PathDirect, Row.ReasonNone), (split.Route, split.Reason));
        Assert.Equal((Row.PathTunnel, Row.ReasonNone), (full.Route, full.Reason));
    }

    [Fact]
    public void AnAddressARuleNames_KeepsItsOwnPathAndReason()
    {
        var proxied = HeldRows.Of(Held(RouteVerdict.Proxy, RoutePlan.Tunnel), split: true, name: string.Empty, named: false, datagrams: true);
        var direct = HeldRows.Of(Held(RouteVerdict.Direct, RoutePlan.Permit), split: true, name: string.Empty, named: false, datagrams: true);

        Assert.Equal((Row.PathTunnel, Row.ReasonRange), (proxied.Route, proxied.Reason));
        Assert.Equal((Row.PathDirect, Row.ReasonRange), (direct.Route, direct.Reason));
    }

    [Fact]
    public void AnAdoptedAddress_IsResolvedOnlyWhereANameBroughtIt()
    {
        var byName = HeldRows.Of(Held(plan: RoutePlan.External, adopted: true), split: true, name: "a.example", named: true, datagrams: true);
        var byMode = HeldRows.Of(Held(plan: RoutePlan.External, adopted: true), split: true, name: string.Empty, named: false, datagrams: true);

        Assert.Equal((Row.PathTunnel, Row.ReasonResolved), (byName.Route, byName.Reason));
        Assert.Equal((Row.PathTunnel, Row.ReasonUdp), (byMode.Route, byMode.Reason));
    }

    [Fact]
    public void AnAddressOnlyTheTrackerHolds_ReadsTunnel()
    {
        var row = HeldRows.OfRouted("203.0.113.9", datagrams: true, idleSeconds: 7, name: string.Empty);

        Assert.Equal((Row.PathTunnel, Row.ReasonUdp, 7), (row.Route, row.Reason, row.IdleSeconds));
    }

    [Fact]
    public void TheStandingRanges_ReadByTheirRealPath()
    {
        var server = HeldRows.OfStanding("192.0.2.1/32", past: true);
        var resolver = HeldRows.OfStanding("10.8.0.1/32", past: false);

        Assert.Equal((Row.PathDirect, Row.ReasonService, "direct"), (server.Route, server.Reason, server.Verdict));
        Assert.Equal((Row.PathTunnel, Row.ReasonService, "proxy"), (resolver.Route, resolver.Reason, resolver.Verdict));
    }

    private static RoutingCache.Held Held(RouteVerdict verdict = RouteVerdict.None, RoutePlan plan = RoutePlan.None, bool adopted = false)
    {
        return new RoutingCache.Held(IPAddress.Parse("198.51.100.7"), verdict, plan, plan != RoutePlan.None, adopted, false, false, 4, 300);
    }
}
