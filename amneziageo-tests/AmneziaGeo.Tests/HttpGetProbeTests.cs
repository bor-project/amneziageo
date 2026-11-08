using System.Buffers.Binary;
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

public sealed class HttpGetProbeTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task AProbeUsesSignedBodylessHttp11WithoutSendingItsCredential(bool plain, bool target)
    {
        await using var server = new Front(plain);
        var record = server.Proxy();
        var result = target
            ? await TransportProxyProbes.CheckTargetAsync(new ResolvedTransportProxy(record, "vpn", "vpn.example", 51820),
                CancellationToken.None, connect: server.ConnectAsync).ConfigureAwait(false)
            : await TransportProxyProbes.CheckAsync(record, CancellationToken.None, connect: server.ConnectAsync).ConfigureAwait(false);

        Assert.Equal(TransportProxyAvailability.Available, result.Availability);
        await server.Completion.ConfigureAwait(false);
        Assert.Equal(2, server.Requests.Count);
        Assert.All(server.Requests, request => Assert.DoesNotContain(Front.User, request, StringComparison.Ordinal));
        Assert.Equal(target, server.TargetRequested);
        Assert.Equal(plain ? "" : "tls.example.net", server.ServerName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnAlteredResponseOrNonceCannotConfirmAvailability(bool alterBody)
    {
        await using var server = new Front(true, alterBody: alterBody, wrongNonce: !alterBody);
        var result = alterBody
            ? await TransportProxyProbes.CheckTargetAsync(new ResolvedTransportProxy(server.Proxy(), "vpn", "vpn.example", 51820),
                CancellationToken.None, connect: server.ConnectAsync).ConfigureAwait(false)
            : await TransportProxyProbes.CheckAsync(server.Proxy(), CancellationToken.None,
                connect: server.ConnectAsync).ConfigureAwait(false);

        Assert.Equal(TransportProxyAvailability.Unavailable, result.Availability);
        Assert.Equal("proxy-bad-answer", result.Fault);
        await server.Completion.ConfigureAwait(false);
    }

    [Fact]
    public async Task ASignedDestinationRefusalRetainsItsReason()
    {
        await using var server = new Front(true, denied: true);
        var result = await TransportProxyProbes.CheckTargetAsync(new ResolvedTransportProxy(server.Proxy(), "vpn", "vpn.example", 51820),
            CancellationToken.None, connect: server.ConnectAsync).ConfigureAwait(false);

        Assert.Equal(TransportProxyAvailability.Unavailable, result.Availability);
        Assert.Equal("proxy-target-denied", result.Fault);
        await server.Completion.ConfigureAwait(false);
    }

    [Theory]
    [InlineData(401, TransportProxyAvailability.Unavailable, "proxy-auth")]
    [InlineData(404, TransportProxyAvailability.Unknown, "proxy-probe-unsupported")]
    [InlineData(503, TransportProxyAvailability.Unavailable, "proxy-unavailable")]
    public async Task ASignedStatusCannotBecomeAnAvailableProxy(int status, TransportProxyAvailability availability, string fault)
    {
        await using var server = new Front(true, responseStatus: status);
        var result = await TransportProxyProbes.CheckAsync(server.Proxy(), CancellationToken.None,
            connect: server.ConnectAsync).ConfigureAwait(false);

        Assert.Equal(availability, result.Availability);
        Assert.Equal(fault, result.Fault);
        await server.Completion.ConfigureAwait(false);
    }

    private sealed class Front : IAsyncDisposable
    {
        internal const string User = "ab8e048e-fc1c-4d43-8f5b-82d3d28ea9c3";
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(10));
        private readonly X509Certificate2 _certificate;
        private readonly bool _plain;
        private readonly int _port;
        internal Task Completion { get; }
        internal List<string> Requests { get; } = [];
        internal string ServerName { get; private set; } = "";
        internal bool TargetRequested { get; private set; }

        /// <summary>
        /// ctor
        /// </summary>
        internal Front(bool plain, bool alterBody = false, bool wrongNonce = false, bool denied = false, int responseStatus = 200)
        {
            _plain = plain;
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=tls.example.net", key, HashAlgorithmName.SHA256);
            using var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            _certificate = X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pfx), null);
            _listener.Start();
            _port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Completion = ServeAsync(alterBody, wrongNonce, denied, responseStatus);
        }

        internal CatalogTransportProxy Proxy()
        {
            var proxy = new OfferedProxy("probe", "Probe", "xhttp", "cdn.example.net", _port, "relay",
                new OfferedProxyTls("tls.example.net", _plain ? "" : Convert.ToHexString(SHA256.HashData(_certificate.RawData))),
                new OfferedProxyAuth("vless", User), DateTimeOffset.MinValue,
                new OfferedXHttp("packet-up", "GET", "header") { Scheme = _plain ? "http" : "https" });
            return new CatalogTransportProxy(new ProxyReference(Guid.NewGuid().ToString("D"), "probe"), proxy);
        }

        internal async ValueTask<Stream> ConnectAsync(DnsEndPoint endpoint, CancellationToken ct)
        {
            Assert.Equal("cdn.example.net", endpoint.Host);
            Assert.Equal(_port, endpoint.Port);
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, _port, ct).ConfigureAwait(false);
            return client.GetStream();
        }

        private async Task ServeAsync(bool alterBody, bool wrongNonce, bool denied, int responseStatus)
        {
            var ct = _stop.Token;
            using var client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            using var tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: true);
            if (!_plain)
            {
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ApplicationProtocols = [SslApplicationProtocol.Http11],
                    ServerCertificateSelectionCallback = (_, name) =>
                    {
                        ServerName = name ?? "";
                        return _certificate;
                    },
                }, ct).ConfigureAwait(false);
            }

            var stream = _plain ? client.GetStream() : (Stream)tls;
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
            var epoch = new byte[16];
            var identity = Convert.FromHexString(new HttpGetAuthentication(User).KeyId);
            var challenge = await ReadAsync(reader, ct).ConfigureAwait(false);
            var challengeQuery = Query(challenge.Path);
            Assert.Equal("challenge", challengeQuery["op"]);
            var challengeBody = JsonSerializer.SerializeToUtf8Bytes(new { epoch = Convert.ToHexStringLower(epoch), time = 1000 });
            using var proof = new MemoryStream();
            Put(proof, "amneziageo.http-get.challenge.v2"u8.ToArray());
            Put(proof, "/relay/"u8.ToArray());
            Put(proof, identity);
            Put(proof, Convert.FromHexString(challengeQuery["n"]));
            Put(proof, epoch);
            Number(proof, 1000);
            await WriteAsync(stream, 200, challengeBody, Signature("challenge", proof.ToArray()), epoch, ct).ConfigureAwait(false);

            var probe = await ReadAsync(reader, ct).ConfigureAwait(false);
            var query = Query(probe.Path);
            var path = probe.Path.Split('?')[0];
            Assert.StartsWith("/relay/_amneziageo/probe/", path, StringComparison.Ordinal);
            TargetRequested = query.ContainsKey("host");
            if (TargetRequested)
            {
                Assert.Equal("vpn.example", query["host"]);
                Assert.Equal("51820", query["port"]);
            }

            var auth = new HttpGetAuthentication(User);
            Assert.Equal(auth.KeyId, probe.Headers["X-WG-Key"]);
            var signed = auth.SignRequest(path, query, [], epoch, ulong.Parse(probe.Headers["X-WG-Time"], System.Globalization.CultureInfo.InvariantCulture),
                Convert.FromHexString(probe.Headers["X-WG-Nonce"]));
            Assert.Equal(signed.Signature, probe.Headers["X-WG-Auth"]);
            var nonce = path.Split('/')[^1];
            var body = denied ? JsonSerializer.SerializeToUtf8Bytes(new { error = "proxy-target-denied" })
                : TargetRequested ? JsonSerializer.SerializeToUtf8Bytes(new { nonce, transport = "xhttp", running = true, target = new { host = "vpn.example", port = 51820 }, allowed = true })
                : JsonSerializer.SerializeToUtf8Bytes(new { nonce = wrongNonce ? "wrong" : nonce, transport = "xhttp", running = true });
            var status = denied ? 403 : responseStatus;
            using var response = new MemoryStream();
            Put(response, "amneziageo.http-get.response.v2"u8.ToArray());
            Put(response, signed.Digest);
            var statusBytes = new byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(statusBytes, (ushort)status);
            response.Write(statusBytes);
            Put(response, epoch);
            Number(response, 1000);
            foreach (var text in new[] { "2", "0", "", "" })
            {
                Put(response, Encoding.UTF8.GetBytes(text));
            }
            Put(response, SHA256.HashData(body));
            var signature = Signature("response", response.ToArray());
            await WriteAsync(stream, status, alterBody ? "{}"u8.ToArray() : body, signature, epoch, ct).ConfigureAwait(false);
        }

        private async Task<(string Path, Dictionary<string, string> Headers)> ReadAsync(StreamReader reader, CancellationToken ct)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false) ?? throw new EndOfStreamException();
            var parts = line.Split(' ');
            Assert.Equal("GET", parts[0]);
            Assert.Equal("HTTP/1.1", parts[2]);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var text = new StringBuilder(line);
            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { Length: > 0 } header)
            {
                text.AppendLine(header);
                var pair = header.Split(':', 2);
                headers.Add(pair[0], pair[1].Trim());
            }
            Assert.False(headers.ContainsKey("Authorization"));
            Assert.False(headers.ContainsKey("Upgrade"));
            Assert.False(headers.ContainsKey("Transfer-Encoding"));
            Assert.True(!headers.TryGetValue("Content-Length", out var length) || length == "0");
            Assert.Equal("cdn.example.net:" + _port, headers["Host"]);
            Requests.Add(text.ToString());
            return (parts[1], headers);
        }

        private static Dictionary<string, string> Query(string path) => path.Split('?')[1].Split('&')
            .Select(part => part.Split('=', 2)).ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]));

        private static string Signature(string role, byte[] message)
        {
            var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(User), 32,
                "amneziageo.http-get.v2"u8.ToArray(), Encoding.UTF8.GetBytes(role));
            return Convert.ToBase64String(HMACSHA256.HashData(key, message));
        }

        private static void Put(Stream stream, byte[] value)
        {
            var length = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(length, (uint)value.Length);
            stream.Write(length);
            stream.Write(value);
        }

        private static void Number(Stream stream, ulong value)
        {
            var bytes = new byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
            stream.Write(bytes);
        }

        private static async Task WriteAsync(Stream stream, int status, byte[] body, string signature, byte[] epoch, CancellationToken ct)
        {
            var header = $"HTTP/1.1 {status} OK\r\nContent-Length: {body.Length}\r\nX-WG-Version: 2\r\nX-WG-Epoch: {Convert.ToHexStringLower(epoch)}\r\nX-WG-Time: 1000\r\nX-WG-Seq: 0\r\nX-WG-Auth: {signature}\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct).ConfigureAwait(false);
            await stream.WriteAsync(body, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            var error = await Xunit.Record.ExceptionAsync(() => Completion).ConfigureAwait(false);
            _certificate.Dispose();
            _stop.Dispose();
            if (error is not null && error is not OperationCanceledException)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
            }
        }
    }
}
