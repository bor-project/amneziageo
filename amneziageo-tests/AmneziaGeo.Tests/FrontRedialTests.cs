using AmneziaGeo.Decl;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// A tunnel that stands dialing one websocket front is dialed again once another front holds for its config; the
/// carrier of a tunnel that stands connected is started again at a front that took another port or path.
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

    [Fact]
    public void ACarrierWhoseFrontTookAnotherPortOrPath_IsStartedAgainAtIt()
    {
        Assert.True(FrontRedial.Moved(Named, Named with { PathPrefix = "v2" }));
        Assert.True(FrontRedial.Moved(Named, Named with { Port = 9443 }));
        Assert.True(FrontRedial.Moved(Named, Named with { Port = 9443, PathPrefix = string.Empty }));
    }

    [Fact]
    public void ACarrierWhoseFrontHolds_IsGone_OrIsAFrontOfAnotherKind_IsLeftAlone()
    {
        Assert.False(FrontRedial.Moved(Named, Named));
        Assert.False(FrontRedial.Moved(Named, null));
        Assert.False(FrontRedial.Moved(Named, Named with { Host = "other.example", PathPrefix = "v2" }));
        Assert.False(FrontRedial.Moved(Named, Named with { PathPrefix = "v2", Offered = false }));
        Assert.False(FrontRedial.Moved(Named, Named with { PathPrefix = "v2", Credentials = "user:pass" }));
    }
}
