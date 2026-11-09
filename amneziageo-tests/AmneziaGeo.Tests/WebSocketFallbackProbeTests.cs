using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;
using Xunit;

namespace AmneziaGeo.Tests;

#pragma warning disable xUnit1030

public sealed class WebSocketFallbackProbeTests
{
    [Theory]
    [InlineData(401, false)]
    [InlineData(404, true)]
    public async Task AnOptionalHttpEndpointCannotRejectAValidWebSocket(int optionalStatus, bool configuredTarget)
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

        Assert.Equal(TransportProxyAvailability.Available, result.Availability);
        Assert.Equal("192.0.2.10:51820", await front.Completion.ConfigureAwait(false));
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
        await using var front = new Front(401, 401);

        var result = await TransportProxyProbes.CheckAsync(front.Record(), CancellationToken.None,
            target: new("192.0.2.10", 51820)).ConfigureAwait(false);

        Assert.Equal(TransportProxyAvailability.Unavailable, result.Availability);
        Assert.Equal("proxy-auth", result.Fault);
        await front.Completion.ConfigureAwait(false);
    }

    private sealed class Front : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(8));
        public Task<string> Completion { get; }
        private int Port { get; }

        /// <summary>
        /// ctor
        /// </summary>
        public Front(int optionalStatus, int upgradeStatus)
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Completion = ServeAsync(optionalStatus, upgradeStatus);
        }

        public CatalogTransportProxy Record() => new(new(Guid.NewGuid().ToString("D"), "ws"),
            new("ws", "WS", "websocket", "127.0.0.1", Port, "relay", new("127.0.0.1", "", false),
                new("bearer", "fixture-bearer"), DateTimeOffset.MinValue));

        private async Task<string> ServeAsync(int optionalStatus, int upgradeStatus)
        {
            using (var client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false))
            {
                await HeadersAsync(client).ConfigureAwait(false);
                await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {optionalStatus} Refused\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), _stop.Token).ConfigureAwait(false);
            }

            using var socket = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
            var headers = await HeadersAsync(socket).ConfigureAwait(false);
            Assert.Equal("Bearer fixture-bearer", headers["Authorization"]);
            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(headers["Sec-WebSocket-Key"] + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            var answer = upgradeStatus == 101
                ? $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"
                : $"HTTP/1.1 {upgradeStatus} Refused\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
            await socket.GetStream().WriteAsync(Encoding.ASCII.GetBytes(answer), _stop.Token).ConfigureAwait(false);
            return headers["X-AmneziaGeo-Target"];
        }

        private async Task<Dictionary<string, string>> HeadersAsync(TcpClient client)
        {
            using var reader = new StreamReader(client.GetStream(), Encoding.ASCII, leaveOpen: true);
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

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            await Completion.ConfigureAwait(false);
            _stop.Dispose();
        }
    }
}
