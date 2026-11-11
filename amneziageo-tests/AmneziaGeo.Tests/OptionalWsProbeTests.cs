using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;
using Xunit;

#pragma warning disable xUnit1030

namespace AmneziaGeo.Tests;

public sealed class OptionalWsProbeTests
{
    [Fact]
    public async Task AStalledOptionalGetLeavesTimeForTheWebSocketUpgrade()
    {
        await using var front = new Front();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));

        var result = await TransportProxyProbes.CheckAsync(front.Record, deadline.Token, Resolve).ConfigureAwait(false);

        Assert.True(front.HttpStarted.Task.IsCompletedSuccessfully);
        Assert.Equal(TransportProxyAvailability.Available, result.Availability);
        Assert.Equal(string.Empty, result.Fault);
        var request = await front.WebSocketRequest.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
        Assert.StartsWith("GET /relay/events HTTP/1.1\r\n", request, StringComparison.Ordinal);
        Assert.Contains("Authorization: Bearer test-token\r\n", request, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellingTheCallerDuringTheOptionalGetDoesNotStartAFallback()
    {
        await using var front = new Front();
        using var cancel = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var probing = TransportProxyProbes.CheckAsync(front.Record, cancel.Token, Resolve);
        await front.HttpStarted.Task.WaitAsync(deadline.Token).ConfigureAwait(false);

        await cancel.CancelAsync().ConfigureAwait(false);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probing).ConfigureAwait(false);
        Assert.False(front.WebSocketRequest.Task.IsCompleted);
        Assert.Equal(1, front.Accepted);
    }

    private static Task<IPAddress?> Resolve(string host, CancellationToken ct) => Task.FromResult<IPAddress?>(IPAddress.Loopback);

    private sealed class Front : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(15));
        private readonly X509Certificate2 _certificate;
        private readonly Task _serving;
        private int _accepted;

        public TaskCompletionSource<bool> HttpStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string> WebSocketRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Accepted => Volatile.Read(ref _accepted);
        public CatalogTransportProxy Record { get; }

        /// <summary>
        /// ctor
        /// </summary>
        public Front()
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=tls.example.net", key, HashAlgorithmName.SHA256);
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName("tls.example.net");
            request.CertificateExtensions.Add(names.Build());
            using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            _certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null);
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            var proxy = new OfferedProxy("one", "WebSocket front", "websocket", "front.example.net", port, "relay",
                new OfferedProxyTls("tls.example.net", Convert.ToHexString(SHA256.HashData(_certificate.RawData))),
                new OfferedProxyAuth("bearer", "test-token"), DateTimeOffset.MinValue);
            Record = new CatalogTransportProxy(new ProxyReference(Guid.NewGuid().ToString("D"), "one"), proxy);
            _serving = ServeAsync(_stop.Token);
        }

        private async Task ServeAsync(CancellationToken ct)
        {
            using var first = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            Interlocked.Increment(ref _accepted);
            var stalled = StallHttpAsync(first, ct);
            try
            {
                using var second = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                Interlocked.Increment(ref _accepted);
                await UpgradeAsync(second, ct).ConfigureAwait(false);
            }
            finally
            {
                first.Dispose();
                try
                {
                    await stalled.ConfigureAwait(false);
                }
                catch (Exception ex) when (ct.IsCancellationRequested || ex is IOException or ObjectDisposedException or AuthenticationException)
                {
                }
            }
        }

        private async Task StallHttpAsync(TcpClient client, CancellationToken ct)
        {
            using var tls = new SslStream(client.GetStream());
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = _certificate,
                ApplicationProtocols = [SslApplicationProtocol.Http2],
            }, ct).ConfigureAwait(false);
            var preface = new byte[24];
            await tls.ReadExactlyAsync(preface, ct).ConfigureAwait(false);
            Assert.Equal("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n", Encoding.ASCII.GetString(preface));
            HttpStarted.TrySetResult(true);
            var buffer = new byte[4096];
            while (await tls.ReadAsync(buffer, ct).ConfigureAwait(false) != 0)
            {
            }
        }

        private async Task UpgradeAsync(TcpClient client, CancellationToken ct)
        {
            using var tls = new SslStream(client.GetStream());
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = _certificate }, ct).ConfigureAwait(false);
            var header = new StringBuilder();
            var one = new byte[1];
            while (header.Length < 8192 && !header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                if (await tls.ReadAsync(one, ct).ConfigureAwait(false) == 0)
                {
                    throw new IOException("The WebSocket upgrade ended before its headers.");
                }

                header.Append((char)one[0]);
            }

            WebSocketRequest.TrySetResult(header.ToString());
            var nonce = header.ToString().Split("\r\n", StringSplitOptions.None)
                .Single(line => line.StartsWith("Sec-WebSocket-Key: ", StringComparison.Ordinal))["Sec-WebSocket-Key: ".Length..];
            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(nonce + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            await tls.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
                + "Sec-WebSocket-Accept: " + accept + "\r\nSec-WebSocket-Protocol: v1\r\n\r\n"), ct).ConfigureAwait(false);
            await tls.FlushAsync(ct).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync().ConfigureAwait(false);
            _listener.Stop();
            try
            {
                await _serving.ConfigureAwait(false);
            }
            catch (Exception ex) when (_stop.IsCancellationRequested && ex is OperationCanceledException or SocketException or ObjectDisposedException or IOException)
            {
            }

            _certificate.Dispose();
            _stop.Dispose();
        }
    }
}
