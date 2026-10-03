using System.Net;
using AmneziaGeo.Windows.App;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// Which routes on the tunnel adapter the client leaves to Windows: the ones to the adapter's own addresses.
/// </summary>
public sealed class OwnAddressRouteTests
{
    private const uint Local = 2;
    private const uint NetMgmt = 3;

    [Fact]
    public void AnAddressOfTheTunnelAdapterItself_GetsNoRouteFromTheClient()
    {
        IReadOnlyCollection<IPAddress> own = [IPAddress.Parse("10.8.1.5"), IPAddress.Parse("fdcc:ad94::cafe:10")];

        Assert.True(RouteManager.Own(IPAddress.Parse("10.8.1.5"), own));
        Assert.True(RouteManager.Own(IPAddress.Parse("::ffff:10.8.1.5"), own));
        Assert.True(RouteManager.Own(IPAddress.Parse("fdcc:ad94::cafe:10"), own));
        Assert.False(RouteManager.Own(IPAddress.Parse("10.8.1.1"), own));
        Assert.False(RouteManager.Own(IPAddress.Parse("10.8.1.5"), []));
    }

    [Fact]
    public void OnlyAManagedRoute_IsTakenForTheClients()
    {
        Assert.True(RouteManager.Managed(NetMgmt));
        Assert.False(RouteManager.Managed(Local));
        Assert.False(RouteManager.Managed(0));
    }
}
