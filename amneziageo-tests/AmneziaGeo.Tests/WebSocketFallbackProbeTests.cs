using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;
using AmneziaGeo.Windows.App;
using Xunit;

namespace AmneziaGeo.Tests;

#pragma warning disable xUnit1030

public sealed class WebSocketFallbackProbeTests
{
    [Theory]
    [InlineData(401, false)]
    [InlineData(404, true)]
    public async Task AnOptionalHttpRefusalRemainsAuthoritative(int optionalStatus, bool configuredTarget)
    {
        await using var front = new Front(optionalStatus, 101);
        var target = new ProxyDestination("192.0.2.10", 51820);
        var record = front.Record();
        if (!configuredTarget)
        {
            record = record with { Proxy = record.Proxy with { Targets = new(true, [target]) } };
        }

        var result = await TransportProxyProbes.CheckAsync(record, CancellationToken.None,
            target: configuredTarget ? target : null).ConfigureAwait(false);

        Assert.Equal(optionalStatus == 401 ? TransportProxyAvailability.Unavailable : TransportProxyAvailability.Available, result.Availability);
        Assert.Equal(optionalStatus == 401 ? "proxy-auth" : "", result.Fault);
        Assert.Equal(optionalStatus == 401 ? "" : "192.0.2.10:51820", await front.Completion.ConfigureAwait(false));
        Assert.Equal(optionalStatus == 401 ? 1 : 2, front.Connections);
    }

    [Fact]
    public async Task ARefusedSyntheticDestinationDoesNotBecomeAnAuthorizationFailure()
    {
        await using var front = new Front(404, 400);

        var result = await TransportProxyProbes.CheckAsync(front.Record(), CancellationToken.None).ConfigureAwait(false);

        Assert.Equal(TransportProxyAvailability.Unknown, result.Availability);
        Assert.Equal("proxy-probe-unsupported", result.Fault);
        await front.Completion.ConfigureAwait(false);
    }

    [Fact]
    public async Task AnUpgradeAuthorizationFailureRemainsAnExplicitRefusal()
    {
        await using var front = new Front(404, 401);

        var result = await TransportProxyProbes.CheckAsync(front.Record(), CancellationToken.None,
            target: new("192.0.2.10", 51820)).ConfigureAwait(false);

        Assert.Equal(TransportProxyAvailability.Unavailable, result.Availability);
        Assert.Equal("proxy-auth", result.Fault);
        await front.Completion.ConfigureAwait(false);
    }

    [Theory]
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public async Task ABusyUpgradeRemainsTransientThroughTheActualProbeAndWindowsCarrier(int status)
    {
        foreach (var windows in new[] { false, true })
        {
            await using var front = new Front(404, status);
            var selected = new ResolvedTransportProxy(front.Record(), "qa", "192.0.2.10", 51820);
            if (windows)
            {
                var runner = TunnelProxyFailureBoundaryTests.Runner(null!);
                var error = await Assert.ThrowsAsync<ConnectFailureException>(() => TunnelProxyFailureBoundaryTests.InvokeAsync(runner, "StartCarrierAsync",
                    ["qa", selected, WsEndpoint.From(selected), IPAddress.Loopback, false, "", "", CancellationToken.None])).ConfigureAwait(false);
                Assert.Equal(ConnectFailureReason.UnderlayUnreachable, error.Reason);
            }
            else
            {
                var result = await TransportProxyProbes.CheckAsync(front.Record(), CancellationToken.None,
                    target: new("192.0.2.10", 51820)).ConfigureAwait(false);
                Assert.Equal(TransportProxyAvailability.Unavailable, result.Availability);
                Assert.Equal("proxy-unavailable", result.Fault);
            }

            await front.Completion.ConfigureAwait(false);
            Assert.Equal(2, front.Connections);
        }
    }

    private sealed class Front : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(8));
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly X509Certificate2 _certificate;
        public Task<string> Completion { get; }
        public int Connections { get; private set; }
        private int Port { get; }

        /// <summary>
        /// ctor
        /// </summary>
        public Front(int optionalStatus, int upgradeStatus)
        {
            _certificate = Certificate(_key);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Completion = ServeAsync(optionalStatus, upgradeStatus);
        }

        public CatalogTransportProxy Record() => new(new(Guid.NewGuid().ToString("D"), "ws"),
            new("ws", "WS", "websocket", "127.0.0.1", Port, "relay", new("front.example", Convert.ToHexString(SHA256.HashData(_certificate.RawData))),
                new("bearer", "fixture-bearer"), DateTimeOffset.MinValue));

        private async Task<string> ServeAsync(int optionalStatus, int upgradeStatus)
        {
            using (var client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false))
            {
                Connections++;
                using var tls = new SslStream(client.GetStream());
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _certificate,
                    ApplicationProtocols = [SslApplicationProtocol.Http2],
                }, _stop.Token).ConfigureAwait(false);
                var preface = new byte[24];
                await tls.ReadExactlyAsync(preface, _stop.Token).ConfigureAwait(false);
                Assert.Equal("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n", Encoding.ASCII.GetString(preface));
                await FrameAsync(tls).ConfigureAwait(false);
                await FrameAsync(tls).ConfigureAwait(false);
                var status = Encoding.ASCII.GetBytes(optionalStatus.ToString(System.Globalization.CultureInfo.InvariantCulture));
                await tls.WriteAsync(new byte[] { 0, 0, 0, 4, 0, 0, 0, 0, 0,
                    0, 0, 5, 1, 5, 0, 0, 0, 1, 8, 3, status[0], status[1], status[2] }, _stop.Token).ConfigureAwait(false);
            }

            if (optionalStatus == 401)
            {
                return "";
            }

            using var socket = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
            Connections++;
            using var upgrade = new SslStream(socket.GetStream());
            await upgrade.AuthenticateAsServerAsync(_certificate).ConfigureAwait(false);
            var headers = await HeadersAsync(upgrade).ConfigureAwait(false);
            Assert.Equal("Bearer fixture-bearer", headers["Authorization"]);
            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(headers["Sec-WebSocket-Key"] + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            var answer = upgradeStatus == 101
                ? $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"
                : $"HTTP/1.1 {upgradeStatus} Refused\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
            await upgrade.WriteAsync(Encoding.ASCII.GetBytes(answer), _stop.Token).ConfigureAwait(false);
            return headers["X-AmneziaGeo-Target"];
        }

        private async Task<Dictionary<string, string>> HeadersAsync(Stream stream)
        {
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            while (await reader.ReadLineAsync(_stop.Token).ConfigureAwait(false) is { Length: > 0 } line)
            {
                var colon = line.IndexOf(':');
                if (colon > 0)
                {
                    result[line[..colon]] = line[(colon + 1)..].Trim();
                }
            }

            return result;
        }

        private async Task FrameAsync(Stream stream)
        {
            var header = new byte[9];
            await stream.ReadExactlyAsync(header, _stop.Token).ConfigureAwait(false);
            var data = new byte[(header[0] << 16) | (header[1] << 8) | header[2]];
            await stream.ReadExactlyAsync(data, _stop.Token).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            await Completion.ConfigureAwait(false);
            _stop.Dispose();
            _certificate.Dispose();
            _key.Dispose();
        }

        private static X509Certificate2 Certificate(ECDsa key)
        {
            var request = new CertificateRequest("CN=front.example", key, HashAlgorithmName.SHA256);
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName("front.example");
            request.CertificateExtensions.Add(names.Build());
            using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            return X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null);
        }
    }
}
