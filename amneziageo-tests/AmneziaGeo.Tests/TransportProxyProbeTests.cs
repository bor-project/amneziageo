using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;
using Xunit;

#pragma warning disable xUnit1030

namespace AmneziaGeo.Tests;

/// <summary>
/// Checks HTTP/2 authorization with separate authority and TLS names.
/// </summary>
public sealed class TransportProxyProbeTests
{
    [Theory]
    [InlineData("88")]
    [InlineData("0803323030")]
    [InlineData("08821001")]
    [InlineData("0085b8848d36a303323030")]
    public async Task ACustomTlsName_PreservesThePublicAuthorityAndAuthenticatedGet(string header)
    {
        await using var server = new Front(header, continuation: true, ping: true);
        var record = server.Record(pin: true);

        var result = await TransportProxyProbes.CheckAsync(record, CancellationToken.None, Resolve).ConfigureAwait(false);

        Assert.Equal(TransportProxyAvailability.Available, result.Availability);
        Assert.True(await server.Completion.ConfigureAwait(false));
        Assert.Equal("tls.example.net", server.ServerName);
        Assert.Equal("cdn.example.net:" + server.Port, server.Headers[":authority"]);
        Assert.Equal("GET", server.Headers[":method"]);
        Assert.Equal("https", server.Headers[":scheme"]);
        Assert.StartsWith("/relay/_amneziageo/probe/", server.Headers[":path"], StringComparison.Ordinal);
        Assert.Equal("Bearer " + Front.User, server.Headers["authorization"]);
        Assert.True(server.AuthorizationNeverIndexed);
        Assert.True(server.RequestEnded);
        Assert.True(server.PingAcknowledged);
    }

    [Theory]
    [InlineData("08826801", 401)]
    [InlineData("8d", 404)]
    [InlineData("08836c0cff", 503)]
    public async Task AWebSocketHttp2ProbeReadsFailureStatuses(string header, int status)
    {
        await using var server = new Front(header);
        var response = await XHttpProbeClient.SendAsync(server.Record(pin: true).Proxy, Guid.NewGuid().ToString("N"),
            async (endpoint, ct) =>
            {
                var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, endpoint.Port, ct).ConfigureAwait(false);
                return client.GetStream();
            }, CancellationToken.None).ConfigureAwait(false);

        Assert.Equal(status, response.StatusCode);
        Assert.True(await server.Completion.ConfigureAwait(false));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task AMismatchedNonceOrAnOversizedBody_IsRejected(bool oversized, bool wrongNonce)
    {
        await using var server = new Front("88", oversized: oversized, wrongNonce: wrongNonce);

        var result = await TransportProxyProbes.CheckTargetAsync(new ResolvedTransportProxy(server.Record(pin: true), "vpn", "vpn.example", 443),
            CancellationToken.None, Resolve).ConfigureAwait(false);

        Assert.Equal(TransportProxyAvailability.Unavailable, result.Availability);
        Assert.Equal("proxy-bad-answer", result.Fault);
        Assert.True(await server.Completion.ConfigureAwait(false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnUntrustedCertificateOrWrongPin_IsRejected(bool wrongPin)
    {
        await using var server = new Front("88");
        var record = server.Record(pin: wrongPin);
        if (wrongPin)
        {
            record = record with { Proxy = record.Proxy with { Tls = record.Proxy.Tls with { Pin = new string('0', 64) } } };
        }

        var result = await TransportProxyProbes.CheckAsync(record, CancellationToken.None, Resolve).ConfigureAwait(false);

        Assert.Equal(TransportProxyAvailability.Unavailable, result.Availability);
        Assert.Equal("proxy-tls", result.Fault);
        Assert.Empty(server.Headers);
    }

    [Fact]
    public void NormalTlsValidation_UsesTheExplicitNameAndRequiresHttp2()
    {
        var proxy = new OfferedProxy("probe", "Probe", "websocket", "cdn.example.net", 443, "relay",
            new OfferedProxyTls("tls.example.net", ""), new OfferedProxyAuth("bearer", Front.User), DateTimeOffset.MinValue,
            null);

        var options = XHttpProbeClient.TlsOptions(proxy);

        Assert.Null(options.RemoteCertificateValidationCallback);
        Assert.Equal("tls.example.net", options.TargetHost);
        Assert.Equal([SslApplicationProtocol.Http2], options.ApplicationProtocols);
    }

    [Fact]
    public async Task ARefusedProtectionCallback_ClosesItsSocketBeforeConnect()
    {
        await using var server = new Front("88");
        var held = default(Socket);

        var result = await TransportProxyProbes.CheckAsync(server.Record(pin: true), CancellationToken.None, Resolve,
            socket =>
            {
                held = socket;
                return false;
            }).ConfigureAwait(false);

        Assert.Equal(TransportProxyAvailability.Unavailable, result.Availability);
        Assert.NotNull(held);
        Assert.True(held.SafeHandle.IsClosed);
        Assert.False(server.Accepted);
    }

    [Fact]
    public async Task APhysicalConnector_ReceivesThePublicEndpointAndKeepsCustomSni()
    {
        await using var server = new Front("88");
        var result = await TransportProxyProbes.CheckAsync(server.Record(pin: true), CancellationToken.None,
            connect: async (endpoint, ct) =>
            {
                Assert.Equal("cdn.example.net", endpoint.Host);
                Assert.Equal(server.Port, endpoint.Port);
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, endpoint.Port), ct).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }).ConfigureAwait(false);

        Assert.Equal(TransportProxyAvailability.Available, result.Availability);
        Assert.Equal("tls.example.net", server.ServerName);
        Assert.True(await server.Completion.ConfigureAwait(false));
    }

    [Fact]
    public async Task Http11Only_CannotAcceptTheProbe()
    {
        await using var server = new Front("88", http2: false);

        var result = await TransportProxyProbes.CheckAsync(server.Record(pin: true), CancellationToken.None, Resolve).ConfigureAwait(false);

        Assert.Equal(TransportProxyAvailability.Unavailable, result.Availability);
        Assert.Empty(server.Headers);
    }

    [Fact]
    public void DynamicReferencesAndDuplicateStatus_AreRejected()
    {
        Assert.Throws<InvalidDataException>(() => XHttpProbeClient.HeaderStatus([0xbe]));
        Assert.Throws<InvalidDataException>(() => XHttpProbeClient.HeaderStatus([0x88, 0x88]));
        Assert.Throws<InvalidDataException>(() => XHttpProbeClient.HeaderStatus([0x21, 0x88]));
    }

    private static Task<IPAddress?> Resolve(string host, CancellationToken ct)
    {
        Assert.Equal("cdn.example.net", host);
        return Task.FromResult<IPAddress?>(IPAddress.Loopback);
    }

    [Theory]
    [InlineData("websocket")]
    public async Task ATargetProbeAuthenticatesABodylessGetAndChecksTheExactDestination(string transport)
    {
        await using var server = new Front("88", response: nonce => new
        {
            nonce, transport, running = true, target = new { host = "2001:db8::1", port = 51820 }, allowed = true,
        });
        var record = server.Record(pin: true);
        record = record with { Proxy = record.Proxy with { Transport = transport } };
        var selected = new ResolvedTransportProxy(record, "vpn", "[2001:0db8::1]", 51820);

        var result = await TransportProxyProbes.CheckTargetAsync(selected, CancellationToken.None, Resolve).ConfigureAwait(false);

        Assert.Equal(TransportProxyAvailability.Available, result.Availability);
        Assert.Contains("?host=2001%3Adb8%3A%3A1&port=51820", server.Headers[":path"], StringComparison.Ordinal);
        Assert.Equal("GET", server.Headers[":method"]);
        Assert.True(server.RequestEnded);
        Assert.True(server.AuthorizationNeverIndexed);
        Assert.True(await server.Completion.ConfigureAwait(false));
    }

    [Fact]
    public async Task AnOldGeneralProbeDoesNotConfirmDestinationPermission()
    {
        await using var server = new Front("88");
        var result = await TransportProxyProbes.CheckTargetAsync(new ResolvedTransportProxy(server.Record(pin: true), "vpn", "vpn.example", 443),
            CancellationToken.None, Resolve).ConfigureAwait(false);

        Assert.Equal(TransportProxyAvailability.Unknown, result.Availability);
        Assert.Equal("proxy-probe-unsupported", result.Fault);
    }

    [Theory]
    [InlineData("0803343033", "proxy-target-denied", "proxy-target-denied")]
    [InlineData("0803343033", "access-denied", "proxy-auth")]
    [InlineData("8c", "bad-proxy-target", "bad-proxy-target")]
    public async Task AServerDestinationRefusalRetainsItsReason(string status, string error, string expected)
    {
        await using var server = new Front(status, response: _ => new { error });
        var result = await TransportProxyProbes.CheckTargetAsync(new ResolvedTransportProxy(server.Record(pin: true), "vpn", "vpn.example", 443),
            CancellationToken.None, Resolve).ConfigureAwait(false);

        Assert.Equal(TransportProxyAvailability.Unavailable, result.Availability);
        Assert.Equal(expected, result.Fault);
    }

    [Fact]
    public async Task AModernRestrictedWebSocketUsesAGeneralGetWithoutAFakeUdpTarget()
    {
        await using var server = new Front("88", response: nonce => new { nonce, transport = "websocket", running = true });
        var record = server.Record(pin: true);
        record = record with { Proxy = record.Proxy with { Transport = "websocket", Targets = new(true, [new("vpn.example", 443)]) } };

        var result = await TransportProxyProbes.CheckAsync(record, CancellationToken.None, Resolve).ConfigureAwait(false);

        Assert.Equal(TransportProxyAvailability.Available, result.Availability);
        Assert.DoesNotContain("?host=", server.Headers[":path"], StringComparison.Ordinal);
        Assert.True(server.RequestEnded);
    }

    [Fact]
    public async Task ALocalRecordCannotHideAServerDestinationRefusal()
    {
        await using var server = new Front("0803343033", response: _ => new { error = "proxy-target-denied" });
        var record = server.Record(pin: true) with { Local = true };

        var error = await Assert.ThrowsAsync<TransportProxyFault>(() => TransportProxyProbes.EnsureTargetAsync(
            new ResolvedTransportProxy(record, "vpn", "vpn.example", 443), IPAddress.Loopback, CancellationToken.None)).ConfigureAwait(false);

        Assert.Equal("proxy-target-denied", error.Code);
    }

    [Theory]
    [InlineData("other.example", 443)]
    [InlineData("vpn.example", 444)]
    public async Task AReplyForAnotherDestinationCannotConfirmPermission(string host, int port)
    {
        await using var server = new Front("88", response: nonce => new
        {
            nonce, transport = "websocket", running = true, target = new { host, port }, allowed = true,
        });
        var result = await TransportProxyProbes.CheckTargetAsync(new ResolvedTransportProxy(server.Record(pin: true), "vpn", "vpn.example", 443),
            CancellationToken.None, Resolve).ConfigureAwait(false);

        Assert.Equal(TransportProxyAvailability.Unavailable, result.Availability);
        Assert.Equal("proxy-bad-answer", result.Fault);
    }

    [Fact]
    public async Task AnUnrestrictedLegacyHostnameDoesNotBecomeANewProbeRefusal()
    {
        await using var server = new Front("88");
        var result = await TransportProxyProbes.CheckTargetAsync(new ResolvedTransportProxy(server.Record(pin: true), "vpn", "vpn_node.example", 443),
            CancellationToken.None, connect: (_, _) => throw new InvalidOperationException("The unsupported target must not be probed.")).ConfigureAwait(false);

        Assert.Equal(TransportProxyAvailability.Unknown, result.Availability);
        Assert.Equal("proxy-probe-unsupported", result.Fault);
        Assert.False(server.Accepted);
    }

    private sealed class Front : IAsyncDisposable
    {
        public const string User = "ab8e048e-fc1c-4d43-8f5b-82d3d28ea9c3";
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(8));
        private readonly X509Certificate2 _certificate;
        public Task<bool> Completion { get; }
        public int Port { get; }
        public string ServerName { get; private set; } = "";
        public Dictionary<string, string> Headers { get; } = new(StringComparer.Ordinal);
        public bool AuthorizationNeverIndexed { get; private set; }
        public bool RequestEnded { get; private set; }
        public bool PingAcknowledged { get; private set; }
        public bool Accepted { get; private set; }

        /// <summary>
        /// ctor
        /// </summary>
        public Front(string header, bool continuation = false, bool ping = false, bool oversized = false, bool wrongNonce = false, bool http2 = true,
            Func<string, object>? response = null)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=tls.example.net", key, HashAlgorithmName.SHA256);
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName("tls.example.net");
            request.CertificateExtensions.Add(names.Build());
            using var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            _certificate = X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pfx), null);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Completion = ServeAsync(Convert.FromHexString(header), continuation, ping, oversized, wrongNonce, http2, response);
        }

        public CatalogTransportProxy Record(bool pin)
        {
            var proxy = new OfferedProxy("probe", "Probe", "websocket", "cdn.example.net", Port, "relay",
                new OfferedProxyTls("tls.example.net", pin ? Convert.ToHexString(SHA256.HashData(_certificate.RawData)) : ""),
                new OfferedProxyAuth("bearer", User), DateTimeOffset.MinValue, null);
            return new CatalogTransportProxy(new ProxyReference(Guid.NewGuid().ToString("D"), "probe"), proxy);
        }

        private async Task<bool> ServeAsync(byte[] header, bool continuation, bool ping, bool oversized, bool wrongNonce, bool http2,
            Func<string, object>? response)
        {
            var ct = _stop.Token;
            using var client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            Accepted = true;
            using var tls = new SslStream(client.GetStream());
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ApplicationProtocols = [http2 ? SslApplicationProtocol.Http2 : SslApplicationProtocol.Http11],
                ServerCertificateSelectionCallback = (_, name) =>
                {
                    ServerName = name ?? "";
                    return _certificate;
                },
            }, ct).ConfigureAwait(false);
            if (!http2)
            {
                Assert.NotEqual(SslApplicationProtocol.Http2, tls.NegotiatedApplicationProtocol);
                var one = new byte[1];
                return await tls.ReadAsync(one, ct).ConfigureAwait(false) == 0;
            }

            Assert.Equal(SslApplicationProtocol.Http2, tls.NegotiatedApplicationProtocol);
            var preface = new byte[24];
            await tls.ReadExactlyAsync(preface, ct).ConfigureAwait(false);
            Assert.Equal("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n", Encoding.ASCII.GetString(preface));
            var settings = await ReadAsync(tls, ct).ConfigureAwait(false);
            Assert.Equal(4, settings.Type);
            Assert.Equal(0, settings.Stream);
            Assert.Equal(0, BinaryPrimitives.ReadInt32BigEndian(settings.Payload.AsSpan(2, 4)));
            var head = await ReadAsync(tls, ct).ConfigureAwait(false);
            Assert.Equal(1, head.Type);
            Assert.Equal(1, head.Stream);
            RequestEnded = (head.Flags & 1) != 0;
            ReadHeaders(head.Payload);
            await WriteAsync(tls, 4, 0, 0, [], ct).ConfigureAwait(false);
            await WriteAsync(tls, 4, 1, 0, [], ct).ConfigureAwait(false);
            if (ping)
            {
                await WriteAsync(tls, 6, 0, 0, "12345678"u8.ToArray(), ct).ConfigureAwait(false);
            }

            if (continuation)
            {
                await WriteAsync(tls, 1, 0, 1, [0x20], ct).ConfigureAwait(false);
                await WriteAsync(tls, 9, 4, 1, header, ct).ConfigureAwait(false);
            }
            else
            {
                await WriteAsync(tls, 1, 4, 1, header, ct).ConfigureAwait(false);
            }

            var nonce = Headers[":path"].Split('?')[0].Split('/')[^1];
            var body = oversized ? new byte[4097] : response is not null ? JsonSerializer.SerializeToUtf8Bytes(response(nonce)) : JsonSerializer.SerializeToUtf8Bytes(new
            {
                nonce = wrongNonce ? "other" : nonce, transport = "websocket", running = true,
            });
            await WriteAsync(tls, 0, 1, 1, body, ct).ConfigureAwait(false);
            try
            {
                while (true)
                {
                    var next = await ReadAsync(tls, ct).ConfigureAwait(false);
                    Assert.Equal(0, next.Stream);
                    Assert.Contains(next.Type, new byte[] { 4, 6 });
                    Assert.Equal(1, next.Flags);
                    if (next.Type == 6)
                    {
                        Assert.Equal("12345678", Encoding.ASCII.GetString(next.Payload));
                        PingAcknowledged = true;
                    }
                }
            }
            catch (EndOfStreamException)
            {
                return true;
            }
            catch (IOException)
            {
                return true;
            }
        }

        private void ReadHeaders(byte[] bytes)
        {
            Assert.Equal(0x82, bytes[0]);
            Assert.Equal(0x87, bytes[1]);
            Headers[":method"] = "GET";
            Headers[":scheme"] = "https";
            var offset = 2;
            while (offset < bytes.Length)
            {
                var prefix = bytes[offset];
                var name = ReadInteger(bytes, ref offset, 15);
                var value = ReadString(bytes, ref offset);
                var key = name switch { 1 => ":authority", 4 => ":path", 23 => "authorization", 24 => "cache-control", _ => throw new IOException("Unexpected request header.") };
                Headers.Add(key, value);
                if (name == 23)
                {
                    AuthorizationNeverIndexed = (prefix & 0xf0) == 0x10;
                }
            }
        }

        private static string ReadString(byte[] bytes, ref int offset)
        {
            Assert.Equal(0, bytes[offset] & 128);
            var length = ReadInteger(bytes, ref offset, 127);
            var value = Encoding.UTF8.GetString(bytes, offset, length);
            offset += length;
            return value;
        }

        private static int ReadInteger(byte[] bytes, ref int offset, int mask)
        {
            var value = bytes[offset++] & mask;
            if (value < mask)
            {
                return value;
            }

            for (var shift = 0; ; shift += 7)
            {
                var next = bytes[offset++];
                value += (next & 127) << shift;
                if ((next & 128) == 0)
                {
                    return value;
                }
            }
        }

        private sealed record Frame(byte Type, byte Flags, int Stream, byte[] Payload);

        private static async Task<Frame> ReadAsync(Stream stream, CancellationToken ct)
        {
            var header = new byte[9];
            await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
            var payload = new byte[(header[0] << 16) | (header[1] << 8) | header[2]];
            await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
            return new Frame(header[3], header[4], BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(5)), payload);
        }

        private static async Task WriteAsync(Stream stream, byte type, byte flags, int id, byte[] payload, CancellationToken ct)
        {
            var frame = new byte[payload.Length + 9];
            frame[0] = (byte)(payload.Length >> 16);
            frame[1] = (byte)(payload.Length >> 8);
            frame[2] = (byte)payload.Length;
            frame[3] = type;
            frame[4] = flags;
            BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(5), id);
            payload.CopyTo(frame, 9);
            await stream.WriteAsync(frame, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync().ConfigureAwait(false);
            _listener.Stop();
            try
            {
                await Completion.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or AuthenticationException or SocketException)
            {
            }

            _stop.Dispose();
            _certificate.Dispose();
        }
    }
}
