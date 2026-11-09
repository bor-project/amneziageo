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

    [Theory]
    [InlineData(200)]
    [InlineData(307)]
    public async Task UnsignedProbesUseBodylessGetAndDoNotFollowRedirects(int status)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var requests = 0;
        var serving = ServeAsync();
        var proxy = Definition("http") with { Port = port };

        await Assert.ThrowsAsync<InvalidDataException>(() => XHttpProbeClient.SendAsync(proxy, "nonce", async (endpoint, token) =>
        {
            Assert.Equal("cdn.example.net", endpoint.Host);
            Assert.Equal(port, endpoint.Port);
            requests++;
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), token).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }, timeout.Token)).ConfigureAwait(false);
        var headers = await serving.ConfigureAwait(false);

        Assert.Equal(1, requests);
        Assert.StartsWith("GET /relay/?n=", headers, StringComparison.Ordinal);
        Assert.Contains("&op=challenge HTTP/1.1\r\n", headers, StringComparison.Ordinal);
        Assert.Contains("Host: cdn.example.net:" + port + "\r\n", headers, StringComparison.Ordinal);
        Assert.DoesNotContain("Authorization", headers, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(proxy.Auth.Credential, headers, StringComparison.Ordinal);
        Assert.Contains("X-WG-Key: ", headers, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Transfer-Encoding", headers, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Content-Length", headers, StringComparison.OrdinalIgnoreCase);

        async Task<string> ServeAsync()
        {
            using var connection = await listener.AcceptTcpClientAsync(timeout.Token).ConfigureAwait(false);
            await using var stream = connection.GetStream();
            using var header = new MemoryStream();
            var buffer = new byte[1];
            while (header.Length < 16384)
            {
                await stream.ReadExactlyAsync(buffer, timeout.Token).ConfigureAwait(false);
                header.WriteByte(buffer[0]);
                if (header.Length >= 4 && header.GetBuffer().AsSpan((int)header.Length - 4, 4).SequenceEqual("\r\n\r\n"u8))
                {
                    break;
                }
            }

            var reply = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + " Test\r\nContent-Length: 2\r\nLocation: http://redirect.example.net/\r\nConnection: close\r\n\r\nok");
            await stream.WriteAsync(reply, timeout.Token).ConfigureAwait(false);
            return Encoding.ASCII.GetString(header.ToArray());
        }
    }

    private static OfferedProxy Definition(string? scheme) => new("front", "CDN", "xhttp", "cdn.example.net", 80, "relay",
        new OfferedProxyTls("cdn.example.net", ""), new OfferedProxyAuth("vless", "ab8e048e-fc1c-4d43-8f5b-82d3d28ea9c3"),
        DateTimeOffset.MinValue, new OfferedXHttp("packet-up", "GET", "header", Scheme: scheme));
}
