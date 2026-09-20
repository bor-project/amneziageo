using AmneziaGeo.Decl;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The address a config carried inside a websocket is actually dialed at: the settings where they hold, else the
/// front the config names, else the host and the port of its Endpoint.
/// </summary>
public sealed class WsEndpointTests
{
    private const string Endpoint = "10.99.1.1:51821";
    private const string Front = "wss://front.example:8443/secret";

    [Fact]
    public void NoSettingsAndNoFront_DialTheHostAndThePortOfTheEndpoint()
    {
        var ws = WsEndpoint.Of(string.Empty, 0, "vpn.example:8080", string.Empty);

        Assert.Equal(new WsEndpoint("vpn.example", 8080, string.Empty, string.Empty), ws);
    }

    [Fact]
    public void TheFrontOfTheConfig_StandsForEmptySettings()
    {
        var ws = WsEndpoint.Of(string.Empty, 0, Endpoint, Front);

        Assert.Equal(new WsEndpoint("front.example", 8443, "secret", string.Empty), ws);
    }

    [Fact]
    public void TheSettings_OutweighTheFront()
    {
        Assert.Equal(new WsEndpoint("own.example", 9443, "mine", string.Empty), WsEndpoint.Of("wss://own.example:9443/mine", 0, Endpoint, Front));
        Assert.Equal(new WsEndpoint("own.example", 8443, string.Empty, string.Empty), WsEndpoint.Of("own.example", 0, Endpoint, Front));
        Assert.Equal(new WsEndpoint("front.example", 7443, "secret", string.Empty), WsEndpoint.Of(string.Empty, 7443, Endpoint, Front));
        Assert.Equal(new WsEndpoint("own.example", 7443, string.Empty, string.Empty), WsEndpoint.Of("own.example", 7443, Endpoint, Front));
    }

    [Theory]
    [InlineData("not a host")]
    [InlineData("wss://")]
    [InlineData("wss://:51821")]
    [InlineData("https://front.example/secret")]
    [InlineData("wss://front.example:99999/secret")]
    [InlineData("wss://front.example/secret?x=1")]
    [InlineData("   ")]
    public void BrokenSettings_LeaveTheDefault(string broken)
    {
        Assert.Equal(WsEndpoint.Of(string.Empty, 0, Endpoint, Front), WsEndpoint.Of(broken, 0, Endpoint, Front));
        Assert.Equal(WsEndpoint.Of(string.Empty, 0, Endpoint, string.Empty), WsEndpoint.Of(broken, 0, Endpoint, string.Empty));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(70000)]
    public void ABrokenPort_LeavesTheDefaultPort(int port)
    {
        Assert.Equal(8443, WsEndpoint.Of(string.Empty, port, Endpoint, Front).Port);
        Assert.Equal(51821, WsEndpoint.Of(string.Empty, port, Endpoint, string.Empty).Port);
    }

    [Theory]
    [InlineData("not a front")]
    [InlineData("http://front.example:8443/secret")]
    [InlineData("wss://")]
    public void ABrokenFront_LeavesTheEndpoint(string front)
    {
        Assert.Equal(new WsEndpoint("10.99.1.1", 51821, string.Empty, string.Empty), WsEndpoint.Of(string.Empty, 0, Endpoint, front));
    }

    [Fact]
    public void AFrontWithoutAPort_TakesThePortOfTheEndpoint()
    {
        Assert.Equal(new WsEndpoint("front.example", 51821, "secret", string.Empty), WsEndpoint.Of(string.Empty, 0, Endpoint, "wss://front.example/secret"));
    }

    [Fact]
    public void AnIpv6Endpoint_GivesItsHostWithoutBracketsAndItsPort()
    {
        var ws = WsEndpoint.Of(string.Empty, 0, "[2001:db8::1]:8080", string.Empty);

        Assert.Equal("2001:db8::1", ws.Host);
        Assert.Equal(8080, ws.Port);
    }

    [Fact]
    public void AnEndpointWithoutAPort_LeavesTheWebSocketDefault()
    {
        Assert.Equal(WsEndpoint.DefaultPort, WsEndpoint.Of(string.Empty, 0, "vpn.example", string.Empty).Port);
        Assert.Equal(WsEndpoint.DefaultPort, WsEndpoint.Of(string.Empty, 0, null, null).Port);
    }

    [Fact]
    public void AUrl_CarriesItsOwnPortPathAndCredentials()
    {
        var ws = WsEndpoint.Of("wss://user:pass@front.example.com:8443/secret", 443, "example.net", string.Empty);

        Assert.Equal(new WsEndpoint("front.example.com", 8443, "secret", "user:pass"), ws);
    }

    [Fact]
    public void AHostWithItsOwnPort_KeepsIt()
    {
        Assert.Equal(new WsEndpoint("front.example.com", 8443, string.Empty, string.Empty), WsEndpoint.Of(" front.example.com:8443 ", 443, "example.net", string.Empty));
    }

    [Fact]
    public void TheShownAddress_LeavesOutThePathAndTheCredentials()
    {
        Assert.Equal("front.example.com:8443", WsEndpoint.Of("wss://user:pass@front.example.com:8443/secret", 0, Endpoint, string.Empty).Display());
        Assert.Equal("front.example:8443", WsEndpoint.Of(string.Empty, 0, Endpoint, Front).Display());
    }

    [Fact]
    public void AnAddressOfVersionSix_IsShownInBrackets()
    {
        Assert.Equal("[2001:db8::1]:8080", WsEndpoint.Of(string.Empty, 0, "[2001:db8::1]:8080", string.Empty).Display());
        Assert.Equal("[2001:db8::2]:8443", WsEndpoint.Of("wss://[2001:db8::2]:8443/secret", 0, Endpoint, string.Empty).Display());
    }

    [Fact]
    public void NothingToDial_ShowsNothing()
    {
        Assert.Equal(string.Empty, WsEndpoint.Of(string.Empty, 0, string.Empty, string.Empty).Display());
    }

    [Fact]
    public void TheFront_IsReadFromItsLine()
    {
        const string text = "[Interface]\nPrivateKey = key\n#  amneziageo websocket =  wss://front.example:8443/secret \n# AmneziaGeo Inbound = server\n\n[Peer]\nEndpoint = 10.99.1.1:51821\n";

        Assert.Equal(Front, WsEndpoint.FrontOf(text));
        Assert.Equal(Front, WsEndpoint.FrontOf(text.Replace("\n", "\r\n", StringComparison.Ordinal)));
        Assert.Equal(string.Empty, WsEndpoint.FrontOf("[Interface]\n# AmneziaGeo Inbound = server\n"));
        Assert.Equal(string.Empty, WsEndpoint.FrontOf(null));
        Assert.Equal(new WsEndpoint("front.example", 8443, "secret", string.Empty), WsEndpoint.For(string.Empty, 0, text));
        Assert.Equal(new WsEndpoint("10.99.1.1", 51821, string.Empty, string.Empty), WsEndpoint.For(string.Empty, 0, "[Peer]\nEndpoint = 10.99.1.1:51821\n"));
    }
}
