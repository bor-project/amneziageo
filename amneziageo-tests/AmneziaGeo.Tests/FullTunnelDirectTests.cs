using AmneziaGeo.Routing;
using AmneziaGeo.Windows.App;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// In a full tunnel what stays outside it leaves by the machine's own path: a network of another adapter or of a
/// router of its own, the own network's resolvers and the addresses the own network's names answer with.
/// </summary>
public sealed class FullTunnelDirectTests
{
    [Fact]
    public void ANarrowRouteThroughAnAdapterNotOursIsTheMachinesOwnPath()
    {
        Assert.True(RouteManager.KeepsOwnPath(20, false));
        Assert.True(RouteManager.KeepsOwnPath(24, false));
        Assert.True(RouteManager.KeepsOwnPath(32, false));
    }

    [Fact]
    public void ADefaultRouteOrAnAdapterOfOursLeavesThePathToTheServer()
    {
        Assert.False(RouteManager.KeepsOwnPath(0, false));
        Assert.False(RouteManager.KeepsOwnPath(1, false));
        Assert.False(RouteManager.KeepsOwnPath(24, true));
        Assert.False(RouteManager.KeepsOwnPath(1, true));
    }

    [Fact]
    public void ResolversOutsideTheNetworksKeptOffTheTunnelGetHostRoutes()
    {
        var hosts = TunnelRunner.OffLinkResolvers(["10.0.10.4", "10.0.10.6", "10.0.110.1"], ["1.1.1.1"], ["10.0.110.0/24", "172.17.96.0/20"]);

        Assert.Equal(["10.0.10.4/32", "10.0.10.6/32"], hosts);
    }

    [Fact]
    public void TheTunnelsOwnLoopbackIpv6AndRepeatedResolversGetNone()
    {
        var hosts = TunnelRunner.OffLinkResolvers(["1.1.1.1", "127.0.0.1", "fec0:0:0:ffff::1", "8.8.8.8", "8.8.8.8", "resolver"], ["1.1.1.1"], []);

        Assert.Equal(["8.8.8.8/32"], hosts);
    }

    [Fact]
    public void ANameOfTheOwnNetworkKeepsItsAddressesOnIt()
    {
        Assert.Equal(RouteVerdict.Direct, DnsProxy.AnswerVerdict(RouteVerdict.None, true));
        Assert.Equal(RouteVerdict.None, DnsProxy.AnswerVerdict(RouteVerdict.None, false));
    }

    [Fact]
    public void ARuleByNameStillDecidesForANameOfTheOwnNetwork()
    {
        Assert.Equal(RouteVerdict.Block, DnsProxy.AnswerVerdict(RouteVerdict.Block, true));
        Assert.Equal(RouteVerdict.Proxy, DnsProxy.AnswerVerdict(RouteVerdict.Proxy, true));
        Assert.Equal(RouteVerdict.Direct, DnsProxy.AnswerVerdict(RouteVerdict.Direct, false));
    }
}
