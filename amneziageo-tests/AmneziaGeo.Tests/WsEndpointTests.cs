using AmneziaGeo.Decl;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The address a config carried inside a websocket is actually dialed at. Every probe that wants to know whether
/// the server answers has to ask this first: the endpoint in the config is the port the server hands the tunnel
/// to internally, and asking it directly reads as a dead server on a network that only lets the carrier through.
/// </summary>
public sealed class WsEndpointTests
{
    [Fact]
    public void AnEmptyHost_FallsBackToTheEndpointAndTheSeparatePort()
    {
        var ws = WsEndpoint.Parse(string.Empty, 443, "example.net");

        Assert.Equal("example.net", ws.Host);
        Assert.Equal(443, ws.Port);
        Assert.Equal(string.Empty, ws.PathPrefix);
        Assert.Equal(string.Empty, ws.Credentials);
    }

    [Fact]
    public void ABareHost_KeepsTheSeparatePort()
    {
        var ws = WsEndpoint.Parse(" front.example.com ", 8443, "example.net");

        Assert.Equal("front.example.com", ws.Host);
        Assert.Equal(8443, ws.Port);
    }

    [Fact]
    public void AUrl_CarriesItsOwnPortAndPathToken()
    {
        var ws = WsEndpoint.Parse("wss://front.example.com:8443/secret", 443, "example.net");

        Assert.Equal("front.example.com", ws.Host);
        Assert.Equal(8443, ws.Port);
        Assert.Equal("secret", ws.PathPrefix);
    }
}
