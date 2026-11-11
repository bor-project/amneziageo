using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;
using Xunit;

#pragma warning disable xUnit1030

namespace AmneziaGeo.Tests;

public sealed class TransportProxyWsTests
{
    [Theory]
    [InlineData("vpn.example.net", "vpn.example.net:51820")]
    [InlineData("203.0.113.8", "203.0.113.8:51820")]
    [InlineData("[2001:db8::9]", "[2001:db8::9]:51820")]
    public void ModernHandshakeNamesOneDestinationInTheHeaderAndToken(string target, string header)
    {
        var front = WsEndpoint.From(Resolved(target));
        var request = WsCarrier.Handshake(front, 51820, "test-key", "AmneziaGeo old-token");
        using var claims = Claims(request);

        Assert.True(front.Modern);
        Assert.Contains("GET /relay/events HTTP/1.1\r\n", request, StringComparison.Ordinal);
        Assert.Contains("X-AmneziaGeo-Target: " + header + "\r\n", request, StringComparison.Ordinal);
        Assert.Equal(front.TargetHost.Contains(':') ? "[" + front.TargetHost + "]" : front.TargetHost, claims.RootElement.GetProperty("r").GetString());
        Assert.Equal(51820, claims.RootElement.GetProperty("rp").GetInt32());
        Assert.Contains("Authorization: Bearer modern-token\r\n", request, StringComparison.Ordinal);
        Assert.DoesNotContain("old-token", request, StringComparison.Ordinal);
        Assert.DoesNotContain("Authorization: Basic", request, StringComparison.Ordinal);
    }

    [Fact]
    public void EndpointTextDoesNotExposeTheCredential()
    {
        var front = WsEndpoint.From(Resolved("vpn.example.net"));
        Assert.Equal("cdn.example.net:443", front.ToString());
        Assert.DoesNotContain("modern-token", front.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("user:pass", new WsEndpoint("front.example.net", 443, "relay", "user:pass").ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ModernEndpointNormalizesItsPublicAndTlsNamesAndPin()
    {
        var resolved = Resolved("[2001:0DB8:0:0:0:0:0:9]");
        var definition = resolved.Record.Proxy with
        {
            Host = "CDN.EXAMPLE.NET",
            Tls = new OfferedProxyTls("TLS.EXAMPLE.NET", new string('A', 64)),
        };
        var front = WsEndpoint.From(resolved with { Record = resolved.Record with { Proxy = definition } });

        Assert.Equal("cdn.example.net", front.Host);
        Assert.Equal("2001:db8::9", front.TargetHost);
        Assert.Equal("tls.example.net", front.ServerName);
        Assert.Equal(new string('a', 64), front.Pin);
        Assert.Empty(front.Credentials);
        Assert.False(front.Offered);
        Assert.Equal("tls.example.net", WsCarrier.TlsOptions(front, null).TargetHost);
    }

    [Theory]
    [InlineData("vpn.example.net\r\nInjected: value")]
    [InlineData("vpn.example.net\n")]
    [InlineData("host name")]
    [InlineData("https://vpn.example.net")]
    public void InvalidTargetsCannotEnterTheUpgrade(string target)
    {
        Assert.Throws<ArgumentException>(() => WsEndpoint.From(Resolved(target)));
        var front = new WsEndpoint("cdn.example.net", 443, "relay", TargetHost: target, Bearer: "credential");
        Assert.Throws<ArgumentException>(() => WsCarrier.Handshake(front, 51820, "key", null));
    }

    [Theory]
    [InlineData("credential\r\nInjected: yes")]
    [InlineData("credential with spaces")]
    [InlineData("")]
    public void InvalidBearerCannotEnterTheUpgrade(string bearer)
    {
        var resolved = Resolved("vpn.example.net");
        var definition = resolved.Record.Proxy with { Auth = new OfferedProxyAuth("bearer", bearer) };
        Assert.Throws<ArgumentException>(() => WsEndpoint.From(resolved with { Record = resolved.Record with { Proxy = definition } }));
    }

    [Fact]
    public void LegacyEndpointKeepsBasicAuthorizationAndTheLoopbackTarget()
    {
        var resolved = Resolved("vpn.example.net");
        var definition = resolved.Record.Proxy with { Auth = new OfferedProxyAuth("legacy", "user:pass") };
        var front = WsEndpoint.From(resolved with { Record = resolved.Record with { Proxy = definition, Legacy = true } });
        var request = WsCarrier.Handshake(front, 51820, "key", null);
        using var claims = Claims(request);

        Assert.False(front.Modern);
        Assert.False(front.Offered);
        Assert.Equal("127.0.0.1", front.TargetHost);
        Assert.Equal("127.0.0.1", claims.RootElement.GetProperty("r").GetString());
        Assert.Contains("Authorization: Basic dXNlcjpwYXNz\r\n", request, StringComparison.Ordinal);
        Assert.DoesNotContain("X-AmneziaGeo-Target", request, StringComparison.Ordinal);
        Assert.DoesNotContain("Authorization: Bearer", request, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyOfferedEndpointKeepsTheOriginalTokenAndValidatesTls()
    {
        var resolved = Resolved("vpn.example.net");
        var definition = resolved.Record.Proxy with { Auth = new OfferedProxyAuth("legacy", "") };
        var front = WsEndpoint.From(resolved with { Record = resolved.Record with { Proxy = definition, Legacy = true, LegacyOffered = true } });
        var request = WsCarrier.Handshake(front, 51820, "key", "AmneziaGeo legacy-token");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var certificate = Certificate(key);
        var options = WsCarrier.TlsOptions(front, "AmneziaGeo legacy-token");

        Assert.True(front.Offered);
        Assert.False(front.Modern);
        Assert.Contains("Authorization: AmneziaGeo legacy-token\r\n", request, StringComparison.Ordinal);
        Assert.DoesNotContain("X-AmneziaGeo-Target", request, StringComparison.Ordinal);
        Assert.NotNull(options.RemoteCertificateValidationCallback);
        Assert.False(options.RemoteCertificateValidationCallback(new object(), certificate, null, SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public void LegacyDefaultUpgradeRetainsItsHeaderSequence()
    {
        var request = WsCarrier.Handshake(new WsEndpoint("front.example.net", 8443), 51820, "key", null);
        var token = ProtocolToken(request);
        var expected = "GET /v1/events HTTP/1.1\r\nHost: front.example.net:8443\r\nUpgrade: websocket\r\n"
            + "Connection: Upgrade\r\nSec-WebSocket-Key: key\r\nSec-WebSocket-Version: 13\r\n"
            + "Sec-WebSocket-Protocol: v1, authorization.bearer.<jwt>\r\n\r\n";

        Assert.Equal(expected, request.Replace(token, "<jwt>", StringComparison.Ordinal));
        using var claims = Claims(request);
        Assert.Equal("127.0.0.1", claims.RootElement.GetProperty("r").GetString());
        Assert.Equal(51820, claims.RootElement.GetProperty("rp").GetInt32());
        Assert.Equal(JsonValueKind.Null, claims.RootElement.GetProperty("p").GetProperty("Udp").GetProperty("timeout").ValueKind);
    }

    [Fact]
    public void ModernBearerKeepsNormalCertificateValidationWithoutAPin()
    {
        var front = WsEndpoint.From(Resolved("vpn.example.net"));
        var options = WsCarrier.TlsOptions(front, "AmneziaGeo ignored-token");

        Assert.NotNull(options.RemoteCertificateValidationCallback);
        Assert.Equal("tls.example.net", options.TargetHost);
    }

    [Fact]
    public void AnExplicitPinMatchesOnlyTheRawCertificateDigest()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var certificate = Certificate(key);
        var front = WsEndpoint.From(Resolved("vpn.example.net")) with
        {
            Pin = Convert.ToHexString(SHA256.HashData(certificate.RawData)),
        };
        var options = WsCarrier.TlsOptions(front, null);
        var errors = SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch;
        Assert.NotNull(options.RemoteCertificateValidationCallback);
        Assert.True(options.RemoteCertificateValidationCallback(new object(), certificate, null, SslPolicyErrors.RemoteCertificateChainErrors));
        var mismatched = WsCarrier.TlsOptions(front with { ServerName = "wrong.example.net" }, null);
        Assert.False(mismatched.RemoteCertificateValidationCallback!(new object(), certificate, null, errors));
        var wrong = WsCarrier.TlsOptions(front with { Pin = new string('0', 64) }, null);
        Assert.False(wrong.RemoteCertificateValidationCallback!(new object(), certificate, null, errors));
        Assert.False(options.RemoteCertificateValidationCallback(new object(), null, null, SslPolicyErrors.RemoteCertificateNotAvailable));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1234")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void ModernInvalidPinOrCredentialIsRejected(string pin)
    {
        var front = WsEndpoint.From(Resolved("vpn.example.net")) with { Pin = pin, Bearer = pin.Length == 0 ? "" : "credential" };
        Assert.Throws<ArgumentException>(() => WsCarrier.TlsOptions(front, null));
    }

    [Fact]
    public async Task ARejectedProtectCallbackClosesTheSocketBeforeConnect()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var captured = default(Socket);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await WsCarrier.ProbeAsync(new WsEndpoint("localhost", port), IPAddress.Loopback, 51820, null,
            socket =>
            {
                captured = socket;
                return false;
            }, deadline.Token).ConfigureAwait(false);

        Assert.Equal(WsFrontOutcome.NoAnswer, result.Outcome);
        Assert.NotNull(captured);
        Assert.True(captured.SafeHandle.IsClosed);
        Assert.False(listener.Pending());
    }

    [Fact]
    public async Task AnUnpinnedModernProbeRejectsAnUntrustedCertificate()
    {
        using var server = new TlsPanel("tls.example.net");
        var front = WsEndpoint.From(Resolved("vpn.example.net")) with { Port = server.Port };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var result = await WsCarrier.ProbeAsync(front, IPAddress.Loopback, 51820, () => "AmneziaGeo ignored", null, deadline.Token).ConfigureAwait(false);

        Assert.Equal(WsFrontOutcome.Tls, result.Outcome);
        Assert.Equal(0, server.Served);
    }

    [Fact]
    public async Task AMatchingPinReachesTheHttpUpgradeAndClosesTheProbeSocket()
    {
        using var server = new TlsPanel("tls.example.net");
        var front = WsEndpoint.From(Resolved("vpn.example.net")) with { Port = server.Port, Pin = server.Pin };
        var captured = default(Socket);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var result = await WsCarrier.ProbeAsync(front, IPAddress.Loopback, 51820,
            () => throw new InvalidOperationException("A modern carrier must not request a legacy token."),
            socket =>
            {
                captured = socket;
                return true;
            }, deadline.Token).ConfigureAwait(false);

        Assert.Equal(WsFrontOutcome.Refused, result.Outcome);
        Assert.Equal(1, server.Served);
        Assert.NotNull(captured);
        Assert.True(captured.SafeHandle.IsClosed);
    }

    private static ResolvedTransportProxy Resolved(string target)
    {
        var proxy = new OfferedProxy("one", "relay", "websocket", "cdn.example.net", 443, "relay",
            new OfferedProxyTls("tls.example.net", ""), new OfferedProxyAuth("bearer", "modern-token"), DateTimeOffset.MinValue);
        return new ResolvedTransportProxy(new CatalogTransportProxy(new ProxyReference(Guid.NewGuid().ToString("D"), "one"), proxy),
            "publisher", target, 51820);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AProbeUsesTheTlsNameAndClosesASuccessfulUpgrade(bool legacy)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var certificate = Certificate(key);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var front = WsEndpoint.From(Resolved("vpn.example.net")) with
        {
            Host = "127.0.0.1", Port = port, Pin = Convert.ToHexString(SHA256.HashData(certificate.RawData)), Legacy = legacy, Offered = legacy,
        };
        var named = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var served = UpgradeAsync(listener, certificate, named, deadline.Token);

        try
        {
            var result = await WsCarrier.ProbeAsync(front, IPAddress.Loopback, 51820,
                legacy ? () => "AmneziaGeo fixture" : null, null, deadline.Token).ConfigureAwait(false);

            Assert.True(result.Outcome == WsFrontOutcome.Ok,
                $"Probe: {result.Outcome} ({result.Detail}); upgrade: {served.Status}; server failure: {served.Exception}");
            Assert.Equal("tls.example.net", await named.Task.WaitAsync(deadline.Token).ConfigureAwait(false));
            Assert.True(await served.ConfigureAwait(false));
            TestContext.Current.TestOutputHelper?.WriteLine($"WSS legacy={legacy}: tcp:127.0.0.1 -> tls:SNI=tls.example.net -> pin+SAN+dates -> authenticated upgrade -> socket:closed");
        }
        finally
        {
            await deadline.CancelAsync().ConfigureAwait(false);
            listener.Stop();
            try
            {
                await served.ConfigureAwait(false);
            }
            catch (Exception) when (deadline.IsCancellationRequested)
            {
            }
        }
    }

    private static async Task<bool> UpgradeAsync(TcpListener listener, X509Certificate2 certificate,
        TaskCompletionSource<string> named, CancellationToken ct)
    {
        using var client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
        using var tls = new SslStream(client.GetStream());
        await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificateSelectionCallback = (_, host) =>
            {
                named.TrySetResult(host ?? string.Empty);
                return certificate;
            },
        }, ct).ConfigureAwait(false);
        var header = new StringBuilder();
        var one = new byte[1];
        while (header.Length < 8192 && !header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (await tls.ReadAsync(one, ct).ConfigureAwait(false) == 0)
            {
                return false;
            }

            header.Append((char)one[0]);
        }

        var nonce = header.ToString().Split("\r\n", StringSplitOptions.None)
            .Single(line => line.StartsWith("Sec-WebSocket-Key: ", StringComparison.Ordinal))["Sec-WebSocket-Key: ".Length..];
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(nonce + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        await tls.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
            + "Sec-WebSocket-Accept: " + accept + "\r\nSec-WebSocket-Protocol: v1\r\n\r\n"), ct).ConfigureAwait(false);
        await tls.FlushAsync(ct).ConfigureAwait(false);
        try
        {
            return await tls.ReadAsync(one, ct).ConfigureAwait(false) == 0;
        }
        catch (IOException)
        {
            return true;
        }
    }

    private static X509Certificate2 Certificate(ECDsa key)
    {
        var request = new CertificateRequest("CN=tls.example.net", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("tls.example.net");
        request.CertificateExtensions.Add(names.Build());
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null);
    }

    private static string ProtocolToken(string request) => request.Split("authorization.bearer.", StringSplitOptions.None)[1].Split("\r\n", StringSplitOptions.None)[0];

    private static JsonDocument Claims(string request)
    {
        var value = ProtocolToken(request).Split('.')[1].Replace('-', '+').Replace('_', '/');
        return JsonDocument.Parse(Convert.FromBase64String(value.PadRight(value.Length + ((4 - value.Length % 4) % 4), '=')));
    }
}
