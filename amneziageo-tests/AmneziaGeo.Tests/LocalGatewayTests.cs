using System.Net;

using AmneziaGeo.Ipc;
using AmneziaGeo.Windows.App;

using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The gateway leg of a check measures the router of the house, not an adapter of the client.
/// </summary>
public sealed class LocalGatewayTests
{
    private static readonly IPAddress Own = IPAddress.Parse("172.31.72.2");
    private static readonly IPAddress Router = IPAddress.Parse("192.168.1.1");

    [Fact]
    public void AnAdapterOfTheClient_IsPassedOverForThePhysicalOne()
    {
        var gateway = LocalGateway.Pick([(true, [Own]), (false, [Router])]);

        Assert.Equal("192.168.1.1", gateway);
    }

    [Fact]
    public void WithOnlyTheClientsOwnAdapter_ThereIsNoGatewayToMeasure()
    {
        Assert.Null(LocalGateway.Pick([(true, [Own])]));
    }

    [Fact]
    public void AnAdapterWithoutAnIpv4Hop_IsSkipped()
    {
        var gateway = LocalGateway.Pick([(false, [IPAddress.Any, IPAddress.Parse("fe80::1")]), (false, [Router])]);

        Assert.Equal("192.168.1.1", gateway);
    }

    [Fact]
    public void TheHopOfTheRouteToTheServer_IsTheGateway()
    {
        Assert.Equal("192.168.0.1", PhysicalPath.Choose(IPAddress.Parse("192.168.0.1"), hopOurs: false, "192.168.1.1"));
    }

    [Fact]
    public void AHopOnAnAdapterOfTheClient_GivesWayToTheDeclaredGateway()
    {
        Assert.Equal("192.168.1.1", PhysicalPath.Choose(Own, hopOurs: true, "192.168.1.1"));
    }

    [Fact]
    public void AServerOnTheSameNetwork_LeavesTheDeclaredGateway()
    {
        Assert.Equal("192.168.1.1", PhysicalPath.Choose(null, hopOurs: false, "192.168.1.1"));
        Assert.Null(PhysicalPath.Choose(null, hopOurs: false, null));
    }
}
