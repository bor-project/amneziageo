using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AmneziaGeo.Routing;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Proxy the gateway hands every connection of the access point's clients to. It turns a stand-in address back
/// into the name it was handed out for and opens that name as an ordinary socket of this machine, so the routing
/// table decides whether it rides the tunnel - the same decision this machine's own traffic gets. It listens on
/// the loopback and asks for no account, so nothing off this machine reaches it.
/// </summary>
internal sealed class HotspotProxy : IDisposable
{
    private const byte Version5 = 0x05;
    private const byte NoAuth = 0x00;
    private const byte NoMethod = 0xFF;
    private const byte CommandConnect = 0x01;
    private const byte CommandAssociate = 0x03;
    private const byte AddressIpV4 = 0x01;
    private const byte AddressName = 0x03;
    private const byte AddressIpV6 = 0x04;
    private const byte ReplyOk = 0x00;
    private const byte ReplyBlocked = 0x02;
    private const byte ReplyRefused = 0x05;
    private const byte ReplyNoCommand = 0x07;
    private const int SioUdpConnReset = unchecked((int)0x9800000C);
    private const int GreetTimeoutMs = 10_000;
    private const int ResolveTimeoutMs = 5000;
    private const int RelayBuffer = 65535;
    private const int Backlog = 128;
    // The port is taken out of a window of its own instead of letting Windows pick. A listener on a port of the
    // dynamic range collides with the source port the gateway's own connection is given, and Windows refuses
    // that connection as an address already in use.
    private const int FirstPort = 45123;
    private const int PortWindow = 100;
    // A flow of datagrams nothing has passed through for this long is dropped with its socket.
    private const int FlowIdleMs = 120_000;
    private const int SweepIntervalMs = 30_000;

    private readonly HotspotNames _names;
    private readonly IProxyOutbound _outbound;
    private readonly ILogger _logger;
    private readonly object _sync = new();
    private Socket? _listener;
    private CancellationTokenSource? _life;

    /// <summary>
    /// ctor
    /// </summary>
    public HotspotProxy(HotspotNames names, IProxyOutbound outbound, ILogger logger)
    {
        _names = names;
        _outbound = outbound;
        _logger = logger;
    }

    /// <summary>
    /// Loopback port the gateway reaches the proxy at; zero while it is not listening.
    /// </summary>
    public int Port { get; private set; }

    /// <summary>
    /// Takes a loopback port and starts serving; false when no port could be taken.
    /// </summary>
    public bool Start()
    {
        lock (_sync)
        {
            if (_listener is not null)
            {
                return true;
            }

            try
            {
                var listener = Bind();
                _listener = listener;
                _life = new CancellationTokenSource();
                Port = ((IPEndPoint)listener.LocalEndPoint!).Port;
                _ = AcceptAsync(listener, _life.Token);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "the clients of the access point have nowhere to be carried: the proxy the gateway hands them to did not start");
                return false;
            }
        }
    }

    // Takes the first free port of the window; a window taken whole leaves the choice to Windows.
    private static Socket Bind()
    {
        for (var port = FirstPort; port < FirstPort + PortWindow; port++)
        {
            var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                listener.Bind(new IPEndPoint(IPAddress.Loopback, port));
                listener.Listen(Backlog);
                return listener;
            }
            catch (SocketException)
            {
                listener.Dispose();
            }
        }

        var any = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        any.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        any.Listen(Backlog);
        return any;
    }

    /// <summary>
    /// Stops serving and drops the port.
    /// </summary>
    public void Stop()
    {
        lock (_sync)
        {
            _life?.Cancel();
            _life?.Dispose();
            _life = null;
            _listener?.Dispose();
            _listener = null;
            Port = 0;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Stop();
    }

    private async Task AcceptAsync(Socket listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await listener.AcceptAsync(ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return;
            }

            _ = ServeAsync(client, ct);
        }
    }

    private async Task ServeAsync(Socket client, CancellationToken ct)
    {
        try
        {
            client.NoDelay = true;
            using var greeting = CancellationTokenSource.CreateLinkedTokenSource(ct);
            greeting.CancelAfter(GreetTimeoutMs);
            if (!await GreetAsync(client, greeting.Token).ConfigureAwait(false))
            {
                client.Dispose();
                return;
            }

            var head = new byte[3];
            await ReadAsync(client, head, greeting.Token).ConfigureAwait(false);
            var request = await ReadAddressAsync(client, greeting.Token).ConfigureAwait(false);
            switch (head[1])
            {
                case CommandConnect:
                    await ConnectAsync(client, request, ct).ConfigureAwait(false);
                    return;
                case CommandAssociate:
                    await AssociateAsync(client, ct).ConfigureAwait(false);
                    return;
                default:
                    await ReplyAsync(client, ReplyNoCommand, null, ct).ConfigureAwait(false);
                    client.Dispose();
                    return;
            }
        }
        catch (Exception)
        {
            client.Dispose();
        }
    }

    // Opens one destination and carries the connection until either end goes quiet.
    private async Task ConnectAsync(Socket client, Request request, CancellationToken ct)
    {
        var target = Target(request);
        _logger.LogDebug("a client of the access point is opening {Target}:{Port}", target, request.Port);
        var (link, outcome) = await _outbound.ConnectAsync(target, request.Port, ct).ConfigureAwait(false);
        var reply = outcome switch
        {
            ProxyOutcome.Ok => ReplyOk,
            ProxyOutcome.Blocked => ReplyBlocked,
            _ => ReplyRefused,
        };
        await ReplyAsync(client, reply, null, ct).ConfigureAwait(false);
        if (link is null)
        {
            client.Dispose();
            return;
        }

        try
        {
            using (link)
            using (client)
            {
                await using var near = new NetworkStream(client, ownsSocket: false);
                await using var far = new NetworkStream(link.Socket, ownsSocket: false);
                var up = CarryAsync(near, far, ct);
                var down = CarryAsync(far, near, ct);
                await Task.WhenAny(up, down).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
        }
    }

    // One direction of a connection, until it goes quiet.
    private static async Task CarryAsync(Stream from, Stream to, CancellationToken ct)
    {
        try
        {
            await from.CopyToAsync(to, RelayBuffer, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    // Holds a datagram relay for as long as the connection that asked for it stands.
    private async Task AssociateAsync(Socket control, CancellationToken ct)
    {
        using var relay = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        relay.Client.IOControl(SioUdpConnReset, new byte[4], null);
        var bound = (IPEndPoint)relay.Client.LocalEndPoint!;
        await ReplyAsync(control, ReplyOk, bound, ct).ConfigureAwait(false);

        using var life = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var flows = new ConcurrentDictionary<string, Flow>(StringComparer.Ordinal);
        var pump = PumpAsync(relay, flows, life.Token);
        var sweep = SweepAsync(flows, life.Token);
        try
        {
            using (control)
            {
                var idle = new byte[1];
                while (await control.ReceiveAsync(idle, SocketFlags.None, ct).ConfigureAwait(false) > 0)
                {
                }
            }
        }
        catch (Exception)
        {
        }
        finally
        {
            life.Cancel();
            foreach (var flow in flows.Values)
            {
                flow.Dispose();
            }

            try
            {
                await pump.ConfigureAwait(false);
                await sweep.ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }
    }

    // Drops the flows nothing passes through any more, so a client that opens many of them leaves no sockets
    // behind.
    private static async Task SweepAsync(ConcurrentDictionary<string, Flow> flows, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(SweepIntervalMs, ct).ConfigureAwait(false);
                foreach (var pair in flows)
                {
                    if (!pair.Value.Idle)
                    {
                        continue;
                    }

                    if (flows.TryRemove(new KeyValuePair<string, Flow>(pair.Key, pair.Value)))
                    {
                        pair.Value.Dispose();
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // Carries out what the gateway sends, each destination on a socket of its own so an answer can be stamped
    // with the address the client sent to.
    private async Task PumpAsync(UdpClient relay, ConcurrentDictionary<string, Flow> flows, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await relay.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return;
            }

            if (!TryUnpack(received.Buffer, out var request, out var wrapper, out var payload))
            {
                continue;
            }

            var key = string.Concat(received.RemoteEndPoint.ToString(), "|", request.Host, ":", request.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (flows.TryGetValue(key, out var flow))
            {
                await flow.SendAsync(payload, ct).ConfigureAwait(false);
                continue;
            }

            var opened = new Flow(wrapper, relay, received.RemoteEndPoint);
            if (!flows.TryAdd(key, opened))
            {
                opened.Dispose();
                continue;
            }

            // Resolving off this loop keeps one slow name from holding up every other flow of the association.
            // What arrives for this destination meanwhile is dropped and the client sends it again.
            _ = OpenAsync(opened, Target(request), request.Port, payload, ct);
        }
    }

    // Resolves the destination, carries the first datagram and reads the answers back.
    private async Task OpenAsync(Flow flow, string target, int port, byte[] payload, CancellationToken ct)
    {
        try
        {
            using var resolving = CancellationTokenSource.CreateLinkedTokenSource(ct);
            resolving.CancelAfter(ResolveTimeoutMs);
            var addresses = IPAddress.TryParse(target, out var literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(target, AddressFamily.InterNetwork, resolving.Token).ConfigureAwait(false);
            if (addresses.Length == 0)
            {
                flow.Dispose();
                return;
            }

            flow.Open(new IPEndPoint(addresses[0], port));
            _logger.LogDebug("a client of the access point sent to {Target}:{Port}", target, port);
            await flow.SendAsync(payload, ct).ConfigureAwait(false);
            await flow.ReadAsync(ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            flow.Dispose();
        }
    }

    // The name a stand-in address was handed out for, or what the client named as it is.
    private string Target(Request request)
    {
        if (request.Literal is not null && HotspotNames.Covers(request.Literal) && _names.Name(request.Literal) is { } name)
        {
            return name;
        }

        return request.Host;
    }

    private static async Task<bool> GreetAsync(Socket client, CancellationToken ct)
    {
        var head = new byte[2];
        await ReadAsync(client, head, ct).ConfigureAwait(false);
        if (head[0] != Version5)
        {
            return false;
        }

        var methods = new byte[head[1]];
        if (methods.Length > 0)
        {
            await ReadAsync(client, methods, ct).ConfigureAwait(false);
        }

        var offered = Array.IndexOf(methods, NoAuth) >= 0;
        await client.SendAsync(new byte[] { Version5, offered ? NoAuth : NoMethod }, SocketFlags.None, ct).ConfigureAwait(false);
        return offered;
    }

    private static async Task ReplyAsync(Socket client, byte reply, IPEndPoint? bound, CancellationToken ct)
    {
        var address = bound?.Address.GetAddressBytes() ?? new byte[4];
        var answer = new byte[6 + address.Length];
        answer[0] = Version5;
        answer[1] = reply;
        answer[3] = address.Length == 4 ? AddressIpV4 : AddressIpV6;
        address.CopyTo(answer, 4);
        BinaryPrimitives.WriteUInt16BigEndian(answer.AsSpan(4 + address.Length), (ushort)(bound?.Port ?? 0));
        await client.SendAsync(answer, SocketFlags.None, ct).ConfigureAwait(false);
    }

    private static async Task<Request> ReadAddressAsync(Socket client, CancellationToken ct)
    {
        var kind = new byte[1];
        await ReadAsync(client, kind, ct).ConfigureAwait(false);
        var length = kind[0] switch
        {
            AddressIpV4 => 4,
            AddressIpV6 => 16,
            AddressName => await NameLengthAsync(client, ct).ConfigureAwait(false),
            _ => throw new InvalidOperationException("the gateway named an address of a kind this proxy does not read"),
        };
        var body = new byte[length + 2];
        await ReadAsync(client, body, ct).ConfigureAwait(false);
        return Parse(kind[0], body);
    }

    private static async Task<int> NameLengthAsync(Socket client, CancellationToken ct)
    {
        var size = new byte[1];
        await ReadAsync(client, size, ct).ConfigureAwait(false);
        return size[0];
    }

    // One address followed by its port.
    private static Request Parse(byte kind, ReadOnlySpan<byte> body)
    {
        var port = BinaryPrimitives.ReadUInt16BigEndian(body[^2..]);
        var value = body[..^2];
        if (kind == AddressName)
        {
            return new Request(Encoding.ASCII.GetString(value), null, port);
        }

        var address = new IPAddress(value);
        return new Request(address.ToString(), address, port);
    }

    // Reads the destination and the payload out of one wrapped datagram, keeping the wrapper for the answers.
    private static bool TryUnpack(byte[] datagram, out Request request, out byte[] wrapper, out byte[] payload)
    {
        request = default;
        wrapper = [];
        payload = [];
        if (datagram.Length < 7 || datagram[2] != 0)
        {
            return false;
        }

        var body = datagram[3] switch
        {
            AddressIpV4 => 4,
            AddressIpV6 => 16,
            AddressName => datagram[4],
            _ => 0,
        };
        if (body == 0)
        {
            return false;
        }

        var start = datagram[3] == AddressName ? 5 : 4;
        var length = start + body + 2;
        if (datagram.Length < length)
        {
            return false;
        }

        request = Parse(datagram[3], datagram.AsSpan(start, body + 2));
        wrapper = datagram[..length];
        payload = datagram[length..];
        return true;
    }

    private static async Task ReadAsync(Socket client, byte[] buffer, CancellationToken ct)
    {
        var filled = 0;
        while (filled < buffer.Length)
        {
            var read = await client.ReceiveAsync(buffer.AsMemory(filled), SocketFlags.None, ct).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("the gateway closed the connection before the request was whole");
            }

            filled += read;
        }
    }

    /// <summary>
    /// One destination of one association: the socket it is reached on and the wrapper its answers carry back.
    /// </summary>
    private sealed class Flow(byte[] wrapper, UdpClient relay, IPEndPoint gateway) : IDisposable
    {
        private readonly UdpClient _socket = new(new IPEndPoint(IPAddress.Any, 0));
        private volatile IPEndPoint? _target;
        private long _touched = Environment.TickCount64;

        /// <summary>
        /// Whether nothing has passed through for the whole idle span.
        /// </summary>
        public bool Idle => Environment.TickCount64 - Interlocked.Read(ref _touched) > FlowIdleMs;

        /// <summary>
        /// Points the flow at the destination the name resolved to.
        /// </summary>
        public void Open(IPEndPoint target)
        {
            _socket.Client.IOControl(SioUdpConnReset, new byte[4], null);
            _target = target;
        }

        /// <summary>
        /// Sends one datagram out; nothing leaves before the destination is known.
        /// </summary>
        public async Task SendAsync(byte[] payload, CancellationToken ct)
        {
            var target = _target;
            if (target is null)
            {
                return;
            }

            Interlocked.Exchange(ref _touched, Environment.TickCount64);
            try
            {
                await _socket.SendAsync(payload, target, ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// Carries answers back to the gateway until the flow is dropped.
        /// </summary>
        public async Task ReadAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                var received = await _socket.ReceiveAsync(ct).ConfigureAwait(false);
                Interlocked.Exchange(ref _touched, Environment.TickCount64);
                var answer = new byte[wrapper.Length + received.Buffer.Length];
                wrapper.CopyTo(answer, 0);
                received.Buffer.CopyTo(answer, wrapper.Length);
                await relay.SendAsync(answer, gateway, ct).ConfigureAwait(false);
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            _socket.Dispose();
        }
    }

    /// <summary>
    /// One destination as the client named it.
    /// </summary>
    private readonly record struct Request(string Host, IPAddress? Literal, int Port);
}
