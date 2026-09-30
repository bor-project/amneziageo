using System.Net;
using AmneziaGeo.Windows.App;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The server address a dial checks the way to before it starts the tunnel: the one the configuration names, else
/// the one that worked last time.
/// </summary>
public sealed class ServerAddressTests
{
    [Theory]
    [InlineData("203.0.113.7:51820", null, "203.0.113.7")]
    [InlineData("203.0.113.7:51820", "198.51.100.9", "203.0.113.7")]
    [InlineData("vpn.example.net:51820", "198.51.100.9", "198.51.100.9")]
    [InlineData("[2001:db8::1]:51820", null, "2001:db8::1")]
    public void TheServer_IsFound(string endpoint, string? cached, string expected)
    {
        Assert.Equal(IPAddress.Parse(expected), ConfigRunner.ServerAddress(endpoint, cached));
    }

    [Theory]
    [InlineData("vpn.example.net:51820", null)]
    [InlineData("vpn.example.net:51820", "")]
    [InlineData("vpn.example.net:51820", "not an address")]
    [InlineData(null, "198.51.100.9")]
    [InlineData(" ", null)]
    public void AnUnknownServer_IsNotGuessed(string? endpoint, string? cached)
    {
        Assert.Null(ConfigRunner.ServerAddress(endpoint, cached));
    }
}
