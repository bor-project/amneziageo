using AmneziaGeo.Decl;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The address a config carried inside a websocket is actually dialed at: the front its server offers where the
/// server answered, else the front the config names, else the settings, else the host and the port of its Endpoint.
/// </summary>
public sealed class WsEndpointTests
{
    private const string Endpoint = "10.99.1.1:51821";
    private const string Front = "wss://front.example:8443/secret";
    private const string Text = "[Interface]\nPrivateKey = key\n\n[Peer]\nPublicKey = server\nEndpoint = vpn.example:51820\n";
    private const string Named = "[Interface]\nPrivateKey = key\n# AmneziaGeo WebSocket = wss://front.example:8443/secret\n\n[Peer]\nPublicKey = server\nEndpoint = vpn.example:51820\n";

    private static readonly ServerOffer Offered =
        ServerOffer.Parse("""{"server":"amneziageo","version":"1","client":"c","features":{"websocket":{"port":8446}}}""");

    private static readonly ServerOffer NoFront =
        ServerOffer.Parse("""{"server":"amneziageo","version":"1","client":"c","features":{"routing":{"allowed":true}}}""");

    private static readonly ConfigTransport Settings = new("c", true, WebSocketHost: "wss://user:pass@own.example:9443/mine", WebSocketPort: 0);

    [Fact]
    public void TheFrontTheServerOffers_IsTheHostOfTheEndpointAtThatPort()
    {
        var front = WsEndpoint.Of(Text, Offered, Settings);

        Assert.Equal(new WsEndpoint("vpn.example", 8446, Offered: true), front);
        Assert.Equal("vpn.example:8446", front?.Display());
        Assert.Equal(WsSource.Server, WsEndpoint.SourceOf(Named, Offered));
    }

    [Fact]
    public void AServerOfOursThatOffersNoFront_LeavesNoneWhateverTheConfigAndTheSettingsName()
    {
        Assert.Null(WsEndpoint.Of(Named, NoFront, Settings));
        Assert.Null(WsEndpoint.Of("[Interface]\nPrivateKey = key\n", Offered, null));
    }

    [Fact]
    public void TheFrontTheConfigNames_OutweighsTheSettingsOfAServerThatIsNotOurs()
    {
        Assert.Equal(new WsEndpoint("front.example", 8443, "secret"), WsEndpoint.Of(Named, ServerOffer.None, Settings));
        Assert.Equal(new WsEndpoint("front.example", 8443, "secret"), WsEndpoint.Of(Named, null, null));
        Assert.Equal(WsSource.Config, WsEndpoint.SourceOf(Named, ServerOffer.None));
    }

    [Fact]
    public void TheSettings_CarryTheTunnelOfAServerThatIsNotOurs()
    {
        Assert.Equal(new WsEndpoint("own.example", 9443, "mine", "user:pass"), WsEndpoint.Of(Text, ServerOffer.None, Settings));
        Assert.Equal(new WsEndpoint("vpn.example", 51820), WsEndpoint.Of(Text, null, null));
        Assert.Equal(new WsEndpoint("vpn.example", 9080), WsEndpoint.Of(Text, null, new ConfigTransport("c", true, WebSocketPort: 9080)));
        Assert.Equal(WsSource.Settings, WsEndpoint.SourceOf(Text, null));
        Assert.Null(WsEndpoint.Of("[Interface]\nPrivateKey = key\n", null, null));
    }

    [Fact]
    public void AnEndpointOfVersionSix_IsShownInBrackets()
    {
        var front = WsEndpoint.Of("[Interface]\nPrivateKey = key\n\n[Peer]\nPublicKey = server\nEndpoint = [2001:db8::1]:51820\n", Offered, null);

        Assert.Equal("2001:db8::1", front?.Host);
        Assert.Equal("[2001:db8::1]:8446", front?.Display());
    }

    [Fact]
    public void APortOutsideTheRange_OffersNoFront()
    {
        var broken = ServerOffer.Parse("""{"server":"amneziageo","version":"1","client":"c","features":{"websocket":{"port":70000}}}""");

        Assert.Equal(0, broken.WebSocketPort);
        Assert.Null(WsEndpoint.Of(Text, broken, null));
    }

    [Theory]
    [InlineData("front.example", "", "")]
    [InlineData("front.example", "secret", "")]
    [InlineData("front.example", "", "user:pass")]
    [InlineData("front.example", "secret", "us er:p@ss:w")]
    [InlineData("2001:db8::2", "secret", "user")]
    public void TheAddressOfAFront_NamesItAgain(string host, string path, string credentials)
    {
        var front = new WsEndpoint(host, 8443, path, credentials);
        var read = WsEndpoint.Of(front.Address(), front.Port, Endpoint, string.Empty);

        Assert.Equal(front.Display(), read.Display());
        Assert.Equal(path, read.PathPrefix);
        Assert.Equal(credentials, read.Credentials);
    }

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
    [InlineData("")]
    [InlineData("front.example")]
    [InlineData("front.example:8443")]
    [InlineData("wss://front.example:8443/secret")]
    [InlineData("wss://user:pass@front.example:8443/secret")]
    [InlineData("ws://10.99.1.1:8080")]
    public void AnAddressTheTunnelIsCarriedTo_IsTaken(string address) => Assert.True(WsEndpoint.Dials(address));

    [Theory]
    [InlineData("https://my.example/p")]
    [InlineData("my.example:99999")]
    [InlineData("wss://my.example/p?x=1")]
    [InlineData("wss://my.example/p#f")]
    [InlineData("wss://")]
    public void AnAddressTheClientWillNotDial_IsTurnedDown(string address) => Assert.False(WsEndpoint.Dials(address));

    [Fact]
    public void ThePortACommandSends_IsZeroForNoneAndMinusOneForWhatIsNotAPort()
    {
        Assert.Equal(0, ConfigTransport.PortSent(string.Empty));
        Assert.Equal(0, ConfigTransport.PortSent("0"));
        Assert.Equal(8443, ConfigTransport.PortSent("8443"));
        Assert.Equal(-1, ConfigTransport.PortSent("70000"));
        Assert.Equal(-1, ConfigTransport.PortSent("-1"));
        Assert.Equal(-1, ConfigTransport.PortSent("port"));
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
        Assert.Equal(new WsEndpoint("front.example", 8443, "secret", string.Empty), WsEndpoint.Of(text, null, null));
        Assert.Equal(new WsEndpoint("10.99.1.1", 51821, string.Empty, string.Empty), WsEndpoint.Of("[Peer]\nEndpoint = 10.99.1.1:51821\n", null, null));
    }
}
