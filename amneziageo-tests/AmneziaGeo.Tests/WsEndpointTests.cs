using AmneziaGeo.Decl;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The websocket front a config carries its tunnel to is the host of its Endpoint at the port its server offers.
/// </summary>
public sealed class WsEndpointTests
{
    private static readonly ServerOffer Offered =
        ServerOffer.Parse("""{"server":"amneziageo","version":"1","client":"c","features":{"websocket":{"port":8446}}}""");

    [Fact]
    public void TheFront_IsTheHostOfTheEndpointAtThePortTheServerOffers()
    {
        var front = WsEndpoint.Of("[Peer]\nEndpoint = vpn.example:51820\n", Offered);

        Assert.Equal(new WsEndpoint("vpn.example", 8446), front);
        Assert.Equal("vpn.example:8446", front?.Display());
    }

    [Fact]
    public void AServerThatOffersNoFront_LeavesNone()
    {
        Assert.Null(WsEndpoint.Of("[Peer]\nEndpoint = vpn.example:51820\n", ServerOffer.None));
        Assert.Null(WsEndpoint.Of("[Peer]\nEndpoint = vpn.example:51820\n", null));
        Assert.Null(WsEndpoint.Of("[Interface]\nPrivateKey = key\n", Offered));
    }

    [Fact]
    public void AnEndpointOfVersionSix_IsShownInBrackets()
    {
        var front = WsEndpoint.Of("[Peer]\nEndpoint = [2001:db8::1]:51820\n", Offered);

        Assert.Equal("2001:db8::1", front?.Host);
        Assert.Equal("[2001:db8::1]:8446", front?.Display());
    }

    [Fact]
    public void APortOutsideTheRange_OffersNoFront()
    {
        var broken = ServerOffer.Parse("""{"server":"amneziageo","version":"1","client":"c","features":{"websocket":{"port":70000}}}""");

        Assert.Equal(0, broken.WebSocketPort);
        Assert.Null(WsEndpoint.Of("[Peer]\nEndpoint = vpn.example:51820\n", broken));
    }
}
