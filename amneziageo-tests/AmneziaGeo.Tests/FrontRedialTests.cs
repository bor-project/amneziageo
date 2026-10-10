using AmneziaGeo.Decl;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// A tunnel that stands dialing one websocket front is dialed again once another front holds for its config.
/// </summary>
public sealed class FrontRedialTests
{
    private static readonly WsEndpoint Endpoint = new("vpn.example", 51820);
    private static readonly WsEndpoint Named = new("vpn.example", 8446, "v1", Offered: true);

    [Fact]
    public void ATunnelDialingAnotherFrontThanTheOneThatHolds_IsDialedAgain()
    {
        Assert.True(FrontRedial.Wanted(true, Endpoint, Named));
        Assert.True(FrontRedial.Wanted(true, null, Named));
    }

    [Fact]
    public void ATunnelOnTheFrontThatHolds_WithNoFrontLeft_OrNotDialing_IsLeftAlone()
    {
        Assert.False(FrontRedial.Wanted(true, Named, Named));
        Assert.False(FrontRedial.Wanted(true, Endpoint, null));
        Assert.False(FrontRedial.Wanted(false, Endpoint, Named));
    }
}
