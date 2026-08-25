using System.Net;
using System.Net.Sockets;
using System.Text;
using AmneziaGeo.Routing;
using AmneziaGeo.Windows.App;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The clients of the shared access point are answered with a stand-in address and their traffic is handed to
/// this proxy, which has to turn that address back into the name it was given for and open it as a socket of this
/// machine. Here the destination is a plain echo, so only the stand-in and the protocol are under test.
/// </summary>
public sealed class HotspotProxyTests : IDisposable
{
    private readonly HotspotNames _names = new();
    private readonly EchoStream _stream = new();
    private readonly EchoDatagrams _datagrams = new();
    private readonly List<HotspotProxy> _proxies = [];
    private readonly List<Socket> _clients = [];

    [Fact]
    public void AName_KeepsTheAddressItWasFirstGiven()
    {
        var first = _names.Take("example.test");
        var again = _names.Take("EXAMPLE.TEST.");

        Assert.Equal(first, again);
        Assert.True(HotspotNames.Covers(first));
        Assert.Equal("example.test", _names.Name(first));
        Assert.NotEqual(first, _names.Take("other.test"));
    }

    [Fact]
    public void AnAddressOutsideTheRange_StandsForNothing()
    {
        Assert.False(HotspotNames.Covers(IPAddress.Parse("198.17.255.255")));
        Assert.False(HotspotNames.Covers(IPAddress.Parse("198.20.0.0")));
        Assert.True(HotspotNames.Covers(IPAddress.Parse("198.19.255.255")));
        Assert.Null(_names.Name(IPAddress.Parse("198.18.7.7")));
    }

    [Fact]
    public void EveryAddressHandedOut_IsDroppedWithThePoint()
    {
        var address = _names.Take("example.test");
        _names.Clear();

        Assert.Null(_names.Name(address));
        Assert.Equal(0, _names.Count);
    }

    [Fact]
    public async Task AConnectionToAStandIn_ReachesTheNameItStandsFor()
    {
        var proxy = Listen();
        var stand = _names.Take("localhost");
        var client = await DialAsync(proxy.Port);

        await GreetAsync(client);
        await client.SendAsync(Request(stand, _stream.EndPoint.Port), SocketFlags.None);
        var reply = await ReadAsync(client, 10);

        Assert.Equal(5, reply[0]);
        Assert.Equal(0, reply[1]);
        await client.SendAsync("ping"u8.ToArray(), SocketFlags.None);
        Assert.Equal("ping", Encoding.ASCII.GetString(await ReadAsync(client, 4)));
    }

    [Fact]
    public async Task AConnectionToANameTheGatewayAlreadyKnows_IsOpenedAsItIs()
    {
        var proxy = Listen();
        var client = await DialAsync(proxy.Port);

        await GreetAsync(client);
        await client.SendAsync(Request("localhost", _stream.EndPoint.Port), SocketFlags.None);

        Assert.Equal(0, (await ReadAsync(client, 10))[1]);
        await client.SendAsync("pong"u8.ToArray(), SocketFlags.None);
        Assert.Equal("pong", Encoding.ASCII.GetString(await ReadAsync(client, 4)));
    }

    [Fact]
    public async Task ADestinationThatAnswersNobody_IsRefused()
    {
        var proxy = Listen();
        var client = await DialAsync(proxy.Port);

        await GreetAsync(client);
        // Nothing is listening on the discard port of the loopback.
        await client.SendAsync(Request(IPAddress.Loopback, 9), SocketFlags.None);

        Assert.NotEqual(0, (await ReadAsync(client, 10))[1]);
    }

    [Fact]
    public async Task ACommandTheProxyDoesNotServe_IsTurnedAway()
    {
        var proxy = Listen();
        var client = await DialAsync(proxy.Port);

        await GreetAsync(client);
        // Bind: neither a connect nor a datagram relay.
        await client.SendAsync(Request("localhost", 80, command: 2), SocketFlags.None);

        Assert.Equal(7, (await ReadAsync(client, 10))[1]);
    }

    [Fact]
    public async Task ADatagramToAStandIn_ComesBackFromTheAddressItWasSentTo()
    {
        var proxy = Listen();
        var stand = _names.Take("localhost");
        var control = await DialAsync(proxy.Port);

        await GreetAsync(control);
        await control.SendAsync(Request(IPAddress.Any, 0, command: 3), SocketFlags.None);
        var reply = await ReadAsync(control, 10);
        Assert.Equal(0, reply[1]);

        var relay = new IPEndPoint(new IPAddress(reply[4..8]), (reply[8] << 8) | reply[9]);
        using var sender = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var wrapped = Wrap(stand, _datagrams.EndPoint.Port, "hello"u8.ToArray());
        await sender.SendAsync(wrapped, wrapped.Length, relay);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var answer = await sender.ReceiveAsync(timeout.Token);

        Assert.Equal(wrapped[..^5], answer.Buffer[..^5]);
        Assert.Equal("hello", Encoding.ASCII.GetString(answer.Buffer[^5..]));
    }

    [Fact]
    public async Task WhenTheConnectionThatAskedForTheRelayGoes_TheRelayGoesWithIt()
    {
        var proxy = Listen();
        var control = await DialAsync(proxy.Port);

        await GreetAsync(control);
        await control.SendAsync(Request(IPAddress.Any, 0, command: 3), SocketFlags.None);
        var reply = await ReadAsync(control, 10);
        var relay = new IPEndPoint(new IPAddress(reply[4..8]), (reply[8] << 8) | reply[9]);
        control.Dispose();

        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        await WaitUntilFreeAsync(relay.Port);
        probe.Bind(relay);
    }

    private HotspotProxy Listen()
    {
        var proxy = new HotspotProxy(_names, new DirectProxyOutbound(), NullLogger.Instance);
        _proxies.Add(proxy);
        Assert.True(proxy.Start());
        Assert.NotEqual(0, proxy.Port);
        return proxy;
    }

    // The relay socket goes with its connection, but the close is not instant.
    private static async Task WaitUntilFreeAsync(int port)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                probe.Bind(new IPEndPoint(IPAddress.Loopback, port));
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(100);
            }
        }
    }

    private async Task<Socket> DialAsync(int port)
    {
        var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        _clients.Add(client);
        await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port));
        return client;
    }

    private static async Task GreetAsync(Socket client)
    {
        await client.SendAsync(new byte[] { 5, 1, 0 }, SocketFlags.None);
        Assert.Equal([5, 0], await ReadAsync(client, 2));
    }

    private static byte[] Request(IPAddress address, int port, byte command = 1)
    {
        var octets = address.GetAddressBytes();
        var request = new byte[6 + octets.Length];
        request[0] = 5;
        request[1] = command;
        request[3] = 1;
        octets.CopyTo(request, 4);
        request[^2] = (byte)(port >> 8);
        request[^1] = (byte)(port & 0xFF);
        return request;
    }

    private static byte[] Request(string host, int port, byte command = 1)
    {
        var name = Encoding.ASCII.GetBytes(host);
        var request = new byte[7 + name.Length];
        request[0] = 5;
        request[1] = command;
        request[3] = 3;
        request[4] = (byte)name.Length;
        name.CopyTo(request, 5);
        request[^2] = (byte)(port >> 8);
        request[^1] = (byte)(port & 0xFF);
        return request;
    }

    // One datagram in the wrapper the relay reads the destination from.
    private static byte[] Wrap(IPAddress address, int port, byte[] payload)
    {
        var octets = address.GetAddressBytes();
        var datagram = new byte[6 + octets.Length + payload.Length];
        datagram[3] = 1;
        octets.CopyTo(datagram, 4);
        datagram[4 + octets.Length] = (byte)(port >> 8);
        datagram[5 + octets.Length] = (byte)(port & 0xFF);
        payload.CopyTo(datagram, 6 + octets.Length);
        return datagram;
    }

    private static async Task<byte[]> ReadAsync(Socket socket, int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var buffer = new byte[count];
        var used = 0;
        while (used < count)
        {
            var read = await socket.ReceiveAsync(buffer.AsMemory(used), SocketFlags.None, timeout.Token);
            if (read <= 0)
            {
                break;
            }

            used += read;
        }

        return buffer[..used];
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var client in _clients)
        {
            client.Dispose();
        }

        foreach (var proxy in _proxies)
        {
            proxy.Dispose();
        }

        _stream.Dispose();
        _datagrams.Dispose();
    }

    /// <summary>
    /// Destination that sends back over a connection whatever reaches it.
    /// </summary>
    private sealed class EchoStream : IDisposable
    {
        private readonly Socket _listener = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        private readonly CancellationTokenSource _cts = new();

        /// <summary>
        /// ctor
        /// </summary>
        public EchoStream()
        {
            _listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            _listener.Listen(16);
            EndPoint = (IPEndPoint)_listener.LocalEndPoint!;
            _ = Task.Run(AcceptAsync);
        }

        /// <summary>
        /// Where the proxy opens the connection.
        /// </summary>
        public IPEndPoint EndPoint { get; }

        /// <inheritdoc/>
        public void Dispose()
        {
            _cts.Cancel();
            _listener.Dispose();
            _cts.Dispose();
        }

        private async Task AcceptAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    var client = await _listener.AcceptAsync(_cts.Token);
                    _ = Task.Run(() => EchoAsync(client));
                }
                catch (Exception)
                {
                    return;
                }
            }
        }

        private async Task EchoAsync(Socket client)
        {
            var buffer = new byte[8192];
            try
            {
                using (client)
                {
                    while (!_cts.IsCancellationRequested)
                    {
                        var read = await client.ReceiveAsync(buffer, SocketFlags.None, _cts.Token);
                        if (read <= 0)
                        {
                            return;
                        }

                        await client.SendAsync(buffer.AsMemory(0, read), SocketFlags.None, _cts.Token);
                    }
                }
            }
            catch (Exception)
            {
            }
        }
    }

    /// <summary>
    /// Destination that sends every datagram straight back to whoever sent it.
    /// </summary>
    private sealed class EchoDatagrams : IDisposable
    {
        private readonly UdpClient _socket = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly CancellationTokenSource _cts = new();

        /// <summary>
        /// ctor
        /// </summary>
        public EchoDatagrams()
        {
            EndPoint = (IPEndPoint)_socket.Client.LocalEndPoint!;
            _ = Task.Run(EchoAsync);
        }

        /// <summary>
        /// Where the relay sends the datagrams.
        /// </summary>
        public IPEndPoint EndPoint { get; }

        /// <inheritdoc/>
        public void Dispose()
        {
            _cts.Cancel();
            _socket.Dispose();
            _cts.Dispose();
        }

        private async Task EchoAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    var received = await _socket.ReceiveAsync(_cts.Token);
                    await _socket.SendAsync(received.Buffer, received.RemoteEndPoint, _cts.Token);
                }
                catch (Exception)
                {
                    return;
                }
            }
        }
    }
}
