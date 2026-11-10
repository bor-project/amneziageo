using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;
using Xunit;

#pragma warning disable xUnit1030

namespace AmneziaGeo.Tests;

/// <summary>
/// Checks explicit HTTP transport selection.
/// </summary>
public sealed class HttpGetSchemeTests
{
    [Theory]
    [InlineData(null, "tls")]
    [InlineData("https", "tls")]
    [InlineData("http", "none")]
    public void ProfilesKeepHttpsUnlessHttpIsExplicit(string? scheme, string security)
    {
        var proxy = Definition(scheme);
        var selected = new ResolvedTransportProxy(new CatalogTransportProxy(new ProxyReference(Guid.NewGuid().ToString("D"), proxy.Id), proxy),
            "vpn", "vpn.example.net", 51820);

        if (scheme == "http")
        {
            Assert.Equal("bad-proxy", Assert.Throws<TransportProxyFault>(() => XHttpProfile.Create(selected, 41000, IPAddress.Loopback)).Code);
            return;
        }

        using var profile = JsonDocument.Parse(XHttpProfile.Create(selected, 41000, IPAddress.Loopback));

        var stream = profile.RootElement.GetProperty("outbounds")[0].GetProperty("streamSettings");
        Assert.Equal(security, stream.GetProperty("security").GetString());
        Assert.Equal(scheme == "http" ? 0 : 3, stream.GetProperty("tlsSettings").EnumerateObject().Count());
    }

    [Fact]
    public void LegacySerializationOmitsTheScheme()
    {
        var serialized = JsonSerializer.Serialize(Definition(null), new JsonSerializerOptions(JsonSerializerDefaults.Web));

        using var document = JsonDocument.Parse(serialized);
        Assert.False(document.RootElement.GetProperty("xhttp").TryGetProperty("scheme", out _));
    }

    [Theory]
    [InlineData("ftp", "")]
    [InlineData("HTTP", "")]
    [InlineData("http", "0000000000000000000000000000000000000000000000000000000000000000")]
    public void InvalidSchemesAndHttpPinsAreRejected(string scheme, string pin)
    {
        var proxy = Definition(scheme) with { Tls = new OfferedProxyTls("cdn.example.net", pin) };
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new OfferedProxyCatalog(1, Guid.NewGuid().ToString("D"), [proxy]),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        Assert.Null(OfferedProxyCatalog.Read(document.RootElement));
    }

    [Fact]
    public async Task PlainHttpProbeIsRejectedWithoutConnect()
    {
        var connected = false;
        await Assert.ThrowsAsync<InvalidDataException>(() => XHttpProbeClient.SendAsync(Definition("http"), "nonce", (_, _) =>
        {
            connected = true;
            throw new InvalidOperationException();
        }, CancellationToken.None)).ConfigureAwait(false);
        Assert.False(connected);
    }

    private static OfferedProxy Definition(string? scheme) => new("front", "CDN", "xhttp", "cdn.example.net", 80, "relay",
        new OfferedProxyTls("cdn.example.net", ""), new OfferedProxyAuth("vless", "ab8e048e-fc1c-4d43-8f5b-82d3d28ea9c3"),
        DateTimeOffset.MinValue, new OfferedXHttp("packet-up", "GET", "header", Scheme: scheme));
}
