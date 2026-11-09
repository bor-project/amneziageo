using System.Text.Json;
using AmneziaGeo.Decl;
using Xunit;

namespace AmneziaGeo.Tests;

#pragma warning disable xUnit1030

public sealed class ProxyTransportModesTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    [Theory]
    [InlineData("GET", false)]
    [InlineData("GET", true)]
    [InlineData("POST", false)]
    [InlineData("POST", true)]
    public void ModernHelloCarriesMethodAndCertificate(string method, bool certificate)
    {
        var item = Http(method, certificate);
        var catalog = Read(2, item);
        Assert.NotNull(catalog);
        Assert.Equal(item, Assert.Single(catalog.Items));
        Assert.Equal(certificate, ProxyTransportModes.UsesCertificate(item));
    }

    [Fact]
    public void InvalidModernFeatureNeverFallsBackToTheOldCatalog()
    {
        var old = JsonSerializer.SerializeToElement(new OfferedProxyCatalog(1, Guid.NewGuid().ToString("D"), [Socket()]), Json);
        var invalid = JsonSerializer.SerializeToElement(new { version = 99, items = Array.Empty<object>() });
        var offer = new ServerOffer("test", "phone", new Dictionary<string, JsonElement>
        {
            [ServerOffer.ProxiesFeature] = old,
            [ServerOffer.ProxiesV2Feature] = invalid,
        });
        Assert.True(offer.HasProxyCatalog);
        Assert.Null(offer.ProxyCatalog);
        Assert.Null(ServerOffer.Parse(offer.ToPayload()).ProxyCatalog);
    }

    [Fact]
    public void ExplicitGetVersionPreservesIdentityAndCredential()
    {
        var old = Http("GET", false) with
        {
            Transport = "xhttp", Http = null, Auth = new OfferedProxyAuth("vless", Http("GET", false).Auth.Credential),
            Xhttp = new OfferedXHttp("packet-up", "GET", "header", Scheme: "http", Version: 2),
        };
        var normalized = Assert.Single(Read(1, old)!.Items);
        Assert.Equal("http", normalized.Transport);
        Assert.Equal(old.Id, normalized.Id);
        Assert.Equal(old.Auth.Credential, normalized.Auth.Credential);
        Assert.Equal("hmac-sha256-v2", normalized.Auth.Scheme);
        Assert.False(normalized.Tls.Enabled);
        Assert.Equal(new OfferedProxyHttp(2, "GET", "header"), normalized.Http);
        var unknown = Assert.Single(Read(1, old with { Xhttp = old.Xhttp! with { Version = 0 } })!.Items);
        Assert.Equal("xhttp", unknown.Transport);
        Assert.True(ProxyTransportModes.Unconfirmed(unknown));
    }

    [Fact]
    public void UnsupportedModesAndQuicWithoutCertificateAreRejected()
    {
        Assert.Null(Read(2, Http("POST", false) with { Http = new OfferedProxyHttp(2, "POST", "header") }));
        Assert.Null(Read(2, Http("GET", true) with { Http = new OfferedProxyHttp(3, "GET", "header") }));
        var quic = Socket() with { Transport = "quic", Quic = new OfferedProxyQuic(1, "webtransport", "framed-stream") };
        Assert.NotNull(Read(2, quic));
        Assert.Null(Read(2, quic with { Tls = quic.Tls with { Enabled = false } }));
        Assert.Null(Read(2, quic with { Quic = quic.Quic! with { Version = 2 } }));
    }

    [Fact]
    public void StoredUpgradePreservesReferencesAndSelections()
    {
        var source = Guid.NewGuid().ToString("D");
        var reference = new ProxyReference(source, "proxy");
        var old = Http("GET", false) with
        {
            Transport = "xhttp", Http = null, Auth = new OfferedProxyAuth("vless", Http("GET", false).Auth.Credential),
            Xhttp = new OfferedXHttp("packet-up", "GET", "header", Scheme: "http", Version: 2),
        };
        var before = new TransportProxyState
        {
            LocalSource = source, Revision = 12,
            Locals = [new CatalogTransportProxy(reference, old, Local: true)],
            Selections = [new TransportProxySelection("vpn", reference)],
        };
        var after = TransportProxyState.Parse(before.ToPayload());
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(reference, Assert.Single(after.Locals).Reference);
        Assert.Equal(reference, Assert.Single(after.Selections).Reference);
        Assert.Equal(old.Auth.Credential, after.Locals[0].Proxy.Auth.Credential);
        Assert.Equal("http", after.Locals[0].Proxy.Transport);
    }

    [Fact]
    public void ActiveTransportKeepsItsOriginalMethod()
    {
        var old = Http("GET", false);
        var reference = new ProxyReference(Guid.NewGuid().ToString("D"), old.Id);
        var resolved = new ResolvedTransportProxy(new CatalogTransportProxy(reference, old), "server", "vpn.example.net", 51820);
        var actual = RunningTransportProxy.From(resolved, "");
        var changed = old with { Http = new OfferedProxyHttp(2, "POST", "body") };
        Assert.Equal("GET", actual.Front!.Http!.Method);
        Assert.False(TransportProxyChanges.SameDefinition(old, changed));
        Assert.False(TransportProxyChanges.SameDefinition(old, old with { Tls = old.Tls with { Enabled = true } }));
    }

    [Fact]
    public async Task PlainWebSocketUsesTheAuthenticatedUpgradeWithoutTls()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var proxy = Socket() with { Port = port, Tls = new OfferedProxyTls("proxy.example.net", "", false) };
        var resolved = new ResolvedTransportProxy(new CatalogTransportProxy(new ProxyReference(Guid.NewGuid().ToString("D"), proxy.Id), proxy), "server", "vpn.example.net", 51820);
        var front = WsEndpoint.From(resolved);
        Assert.False(front.UseCertificate);
        var serving = AnswerAsync();
        var result = await AmneziaGeo.Ipc.WsCarrier.ProbeAsync(front, System.Net.IPAddress.Loopback, 51820, null, null, stop.Token).ConfigureAwait(false);
        Assert.Equal(AmneziaGeo.Ipc.WsFrontOutcome.Ok, result.Outcome);
        await serving.ConfigureAwait(false);

        async Task AnswerAsync()
        {
            using var client = await listener.AcceptTcpClientAsync(stop.Token).ConfigureAwait(false);
            var stream = client.GetStream();
            using var reader = new StreamReader(stream, System.Text.Encoding.ASCII, false, 1024, true);
            var first = await reader.ReadLineAsync(stop.Token).ConfigureAwait(false);
            Assert.StartsWith("GET ", first, StringComparison.Ordinal);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            while (await reader.ReadLineAsync(stop.Token).ConfigureAwait(false) is { Length: > 0 } header)
            {
                var pair = header.Split(':', 2);
                headers[pair[0]] = pair[1].Trim();
            }

            Assert.Equal("Bearer credential", headers["Authorization"]);
            var accept = Convert.ToBase64String(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.ASCII.GetBytes(
                headers["Sec-WebSocket-Key"] + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            var response = "HTTP/1.1 101 Switching Protocols\r\nConnection: Upgrade\r\nUpgrade: websocket\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n";
            await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(response), stop.Token).ConfigureAwait(false);
        }
    }

    [Fact]
    public void UnsupportedNativeModesCannotBecomeGetProfiles()
    {
        var post = Assert.Throws<TransportProxyFault>(() => ProxyTransportModes.GetRuntime(Http("POST", false)));
        Assert.Equal("proxy-platform-unsupported", post.Code);
        var quic = Socket() with { Transport = "quic", Quic = new OfferedProxyQuic(1, "webtransport", "framed-stream") };
        Assert.Equal("proxy-platform-unsupported", Assert.Throws<TransportProxyFault>(() => ProxyTransportModes.GetRuntime(quic)).Code);
    }

    private static OfferedProxyCatalog? Read(int version, OfferedProxy item) => OfferedProxyCatalog.Read(
        JsonSerializer.SerializeToElement(new OfferedProxyCatalog(version, Guid.NewGuid().ToString("D"), [item]), Json));

    internal static OfferedProxy Http(string method, bool certificate) => new("proxy", "test", "http", "proxy.example.net", 80, "relay",
        new OfferedProxyTls("proxy.example.net", "", certificate), new OfferedProxyAuth("hmac-sha256-v2", "22222222-2222-2222-2222-222222222222"),
        DateTimeOffset.MinValue, Http: new OfferedProxyHttp(2, method, method == "GET" ? "header" : "body"));

    private static OfferedProxy Socket() => new("socket", "test", "websocket", "proxy.example.net", 443, "relay",
        new OfferedProxyTls("proxy.example.net", ""), new OfferedProxyAuth("bearer", "credential"), DateTimeOffset.MinValue);
}
