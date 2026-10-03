using System.Net;

using AmneziaGeo.Windows.App;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// A dropped datagram to a new address sends the address into the tunnel where every datagram rides it.
/// </summary>
public sealed class AllUdpDatagramTests
{
    private const uint ProtocolSet = 0x1;
    private const uint RemoteAddressSet = 0x4;
    private const byte Udp = 17;
    private const byte Tcp = 6;

    private static readonly IPAddress Server = IPAddress.Parse("203.0.113.7");
    private static readonly IPAddress Resolver = IPAddress.Parse("8.8.8.8");
    private static readonly IPAddress Stranger = IPAddress.Parse("208.67.222.222");

    [Fact]
    public void ADrop_IsADatagramOnlyWhereItsHeaderCarriesTheProtocol()
    {
        Assert.True(WindowsFirewall.IsDatagram(ProtocolSet | RemoteAddressSet, Udp));
        Assert.False(WindowsFirewall.IsDatagram(RemoteAddressSet, Udp));
        Assert.False(WindowsFirewall.IsDatagram(ProtocolSet | RemoteAddressSet, Tcp));
    }

    [Fact]
    public void ADatagramToAPublicAddress_IsTakenIntoTheTunnel()
    {
        Assert.True(NetworkFlowTracker.TakesDatagram(true, Stranger, Server, new HashSet<IPAddress> { Resolver }));
    }

    [Fact]
    public void TheServerItself_APrivateNetworkAndTheClientsOwnResolver_AreLeftAlone()
    {
        var own = new HashSet<IPAddress> { Resolver };

        Assert.False(NetworkFlowTracker.TakesDatagram(true, Server, Server, own));
        Assert.False(NetworkFlowTracker.TakesDatagram(true, IPAddress.Parse("192.168.1.1"), Server, own));
        Assert.False(NetworkFlowTracker.TakesDatagram(true, Resolver, Server, own));
        Assert.False(NetworkFlowTracker.TakesDatagram(true, IPAddress.Parse("2001:4860:4860::8888"), Server, own));
    }

    [Fact]
    public void WithoutTheRuleForEveryDatagram_NothingIsTaken()
    {
        Assert.False(NetworkFlowTracker.TakesDatagram(false, Stranger, Server, new HashSet<IPAddress>()));
    }

    [Fact]
    public void AnAddressHeldOnARouteOfItsOwn_IsLeftThere()
    {
        var tracker = new NetworkFlowTracker(null, null, true, false, Server, NullLogger<NetworkFlowTracker>.Instance,
            heldElsewhere: address => address.Equals(Stranger));

        Assert.False(tracker.TakeDatagram(Stranger));
    }

    [Fact]
    public void ADestinationTakenOnce_IsNotRoutedAgainUntilItsRouteIsReleased()
    {
        var tracker = new NetworkFlowTracker(null, null, true, false, Server, NullLogger<NetworkFlowTracker>.Instance);

        tracker.MarkTaken(Stranger);

        Assert.True(tracker.Taken(Stranger));
        Assert.True(tracker.TakeDatagram(Stranger));

        tracker.Forget([Stranger.ToString()]);

        Assert.False(tracker.Taken(Stranger));
        Assert.False(tracker.TakeDatagram(Stranger));
    }
}
