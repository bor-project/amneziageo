using System.Net;
using AmneziaGeo.Windows.App;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// A bypass host route for an address on the LAN must stay on-link: sent through the gateway, the /32 outranks the
/// subnet route and the router drops the reply instead of returning it to the segment it came from.
/// </summary>
public sealed class RouteManagerTests
{
    private static readonly string[] Connected = ["10.20.30.0/24", "172.31.16.0/20"];

    [Fact]
    public void NeighbourOnConnectedSubnetIsLocal()
    {
        Assert.True(RouteManager.IsWithinSubnets(IPAddress.Parse("10.20.30.40"), Connected));
        Assert.True(RouteManager.IsWithinSubnets(IPAddress.Parse("172.31.24.5"), Connected));
    }

    [Fact]
    public void PublicAddressIsNotLocal()
    {
        Assert.False(RouteManager.IsWithinSubnets(IPAddress.Parse("203.0.113.9"), Connected));
    }

    [Fact]
    public void PrivateAddressOutsideConnectedSubnetsIsNotLocal()
    {
        // The neighbouring /24 shares the first three octets of a connected subnet and still must not be local.
        Assert.False(RouteManager.IsWithinSubnets(IPAddress.Parse("10.20.31.40"), Connected));
        Assert.False(RouteManager.IsWithinSubnets(IPAddress.Parse("192.168.0.5"), Connected));
    }

    [Fact]
    public void MalformedEntriesAreSkipped()
    {
        string[] cidrs = ["", "/24", "10.20.30.0", "10.20.30.0/", "10.20.30.0/33", "zzz/24", "10.20.30.0/24"];

        Assert.True(RouteManager.IsWithinSubnets(IPAddress.Parse("10.20.30.40"), cidrs));
        Assert.False(RouteManager.IsWithinSubnets(IPAddress.Parse("198.51.100.7"), cidrs));
    }

    [Fact]
    public void V6EntriesAndAddressesAreIgnored()
    {
        Assert.False(RouteManager.IsWithinSubnets(IPAddress.Parse("fe80::1"), Connected));
        Assert.False(RouteManager.IsWithinSubnets(IPAddress.Parse("10.20.30.40"), ["fd00::/8"]));
    }

    [Fact]
    public void EmptySubnetListIsNotLocal()
    {
        Assert.False(RouteManager.IsWithinSubnets(IPAddress.Parse("10.20.30.40"), []));
    }
}
