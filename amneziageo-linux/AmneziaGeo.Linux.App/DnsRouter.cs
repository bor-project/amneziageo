using System.Net;
using System.Net.Sockets;
using AmneziaGeo.Geo;
using AmneziaGeo.Routing;

namespace AmneziaGeo.Linux.App;

/// <summary>
/// Answers the machine's lookups and decides per name, and per address the answer carries, whether the
/// destination goes through the tunnel or stays on the physical path.
/// </summary>
internal sealed class DnsRouter : IDisposable
{
    private const int Port = 53;
    private const int UpstreamTimeoutMs = 4000;
    private const int ConnectionIdleMs = 15000;
    private const int BufferSize = 4096;
    private const int TcpBacklog = 16;
    private const int MaxMessageLength = 65535;

    private readonly bool _split;
    private readonly bool _stripV6;
    private volatile DomainMatcher _proxyDomains;
    private volatile DomainMatcher _directDomains;
    private volatile DomainMatcher _blockDomains;
    private readonly IReadOnlyList<IPAddress> _tunnelResolvers;
    private readonly IReadOnlyList<IPAddress> _lanResolvers;
    private readonly RoutingCache _routes;
    private readonly AgentLog _log;
    private readonly CancellationTokenSource _cts = new();
    private Socket? _udp;
    private Socket? _tcp;
    private bool _disposed;

    /// <summary>
    /// ctor
    /// </summary>
    public DnsRouter(TunnelRouting routing, bool stripV6, IReadOnlyList<IPAddress> tunnelResolvers, IReadOnlyList<IPAddress> lanResolvers, RoutingCache routes, AgentLog log)
    {
        _split = routing.Split;
        _stripV6 = stripV6;
        _proxyDomains = new DomainMatcher(routing.ProxyDomains);
        _directDomains = new DomainMatcher(routing.DirectDomains);
        _blockDomains = new DomainMatcher(routing.BlockDomains);
        _tunnelResolvers = tunnelResolvers;
        _lanResolvers = lanResolvers;
        _routes = routes;
        _log = log;
    }

    /// <summary>
    /// Address the router listens on.
    /// </summary>
    public static IPAddress Listen { get; } = IPAddress.Parse("127.0.0.71");

    /// <summary>
    /// Takes the edited rules over; the next lookup is answered by them.
    /// </summary>
    public void ApplyRules(TunnelRouting routing)
    {
        _proxyDomains = new DomainMatcher(routing.ProxyDomains);
        _directDomains = new DomainMatcher(routing.DirectDomains);
        _blockDomains = new DomainMatcher(routing.BlockDomains);
        _log.Info("dns", $"name rules reloaded: {routing.ProxyDomains.Count} tunneled, {routing.DirectDomains.Count} direct, {routing.BlockDomains.Count} refused");
    }

    /// <summary>
    /// Binds the listeners and starts serving; false when the address is taken.
    /// </summary>
    public bool Start()
    {
        try
        {
            var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            udp.Bind(new IPEndPoint(Listen, Port));
            _udp = udp;
        }
        catch (SocketException ex)
        {
            _log.Error("dns", $"binding {Listen}:{Port} failed", ex);
            return false;
        }

        // A client whose answer did not fit a datagram asks the same question again over TCP.
        try
        {
            var tcp = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            tcp.Bind(new IPEndPoint(Listen, Port));
            tcp.Listen(TcpBacklog);
            _tcp = tcp;
            _ = Task.Run(() => ServeTcpAsync(_cts.Token));
        }
        catch (SocketException ex)
        {
            _log.Warn("dns", $"listening on {Listen}:{Port} over TCP failed, so a truncated answer has nowhere to go: {ex.Message}");
        }

        _ = Task.Run(() => ServeUdpAsync(_cts.Token));
        var ranges = _routes.RangeCounts;
        _log.Info("dns", $"name router on {Listen}:{Port}, {ranges.Proxy} proxy range(s), {ranges.Direct} direct range(s), {ranges.Block} blocked range(s){(_stripV6 ? ", addresses over IPv6 withheld from tunneled names" : string.Empty)}");
        return true;
    }

    // Receives queries and answers each one without holding up the next.
    private async Task ServeUdpAsync(CancellationToken ct)
    {
        var socket = _udp;
        if (socket is null)
        {
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var buffer = new byte[BufferSize];
                var received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), ct).ConfigureAwait(false);
                _ = Task.Run(() => AnswerDatagramAsync(socket, buffer, received.ReceivedBytes, received.RemoteEndPoint, ct), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex)
            {
                _log.Warn("dns", $"receive failed: {ex.Message}");
            }
        }
    }

    private async Task AnswerDatagramAsync(Socket socket, byte[] query, int length, EndPoint client, CancellationToken ct)
    {
        try
        {
            var answer = await ResolveAsync(query, length, overTcp: false, ct).ConfigureAwait(false);
            if (answer is not null)
            {
                await socket.SendToAsync(answer, SocketFlags.None, client, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.Error("dns", "answering a lookup failed", ex);
        }
    }

    // Accepts the connections a client falls back to, serving every query it sends over one.
    private async Task ServeTcpAsync(CancellationToken ct)
    {
        var listener = _tcp;
        if (listener is null)
        {
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptAsync(ct).ConfigureAwait(false);
                _ = Task.Run(() => AnswerStreamAsync(client, ct), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex)
            {
                _log.Warn("dns", $"accepting a connection failed: {ex.Message}");
            }
        }
    }

    private async Task AnswerStreamAsync(Socket client, CancellationToken ct)
    {
        try
        {
            using (client)
            {
                while (true)
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    idle.CancelAfter(ConnectionIdleMs);
                    var query = await ReadFramedAsync(client, idle.Token).ConfigureAwait(false);
                    if (query is null)
                    {
                        return;
                    }

                    var answer = await ResolveAsync(query, query.Length, overTcp: true, ct).ConfigureAwait(false);
                    if (answer is null)
                    {
                        return;
                    }

                    await WriteFramedAsync(client, answer, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException)
        {
        }
        catch (Exception ex)
        {
            _log.Error("dns", "answering a lookup over TCP failed", ex);
        }
    }

    // Decides the query, forwards it to the resolver that fits, and routes what the answer resolved to.
    private async Task<byte[]?> ResolveAsync(byte[] query, int length, bool overTcp, CancellationToken ct)
    {
        if (!DnsWire.TryReadQuestion(query, length, out var name, out var type))
        {
            return null;
        }

        var verdict = Decide(name);
        if (verdict == RouteVerdict.Block)
        {
            return DnsWire.BuildRefusal(query, length);
        }

        // The tunnel carries no IPv6 here, so an address over it would take the destination around the tunnel
        // on a path the rules do not reach.
        if (type == DnsWire.TypeAaaa && WithholdsV6(verdict))
        {
            return DnsWire.BuildEmpty(query, length);
        }

        var upstream = verdict switch
        {
            RouteVerdict.Proxy => _tunnelResolvers,
            RouteVerdict.Direct => _lanResolvers,
            _ => _split ? _lanResolvers : _tunnelResolvers,
        };

        var answer = await AskAsync(upstream, query, length, overTcp, ct).ConfigureAwait(false);
        if (answer is null)
        {
            return null;
        }

        return ApplyAnswer(name, verdict, answer) ? DnsWire.BuildRefusal(query, length) : answer;
    }

    // Everything but a direct destination leaves the rules behind over IPv6 in a full tunnel; in a split only a
    // tunneled one does.
    private bool WithholdsV6(RouteVerdict verdict) =>
        _stripV6 && (verdict == RouteVerdict.Proxy || (!_split && verdict != RouteVerdict.Direct));

    // Hands the addresses the answer carries to the cache, which installs what each one's verdict asks for; true
    // when the destination is blocked and must not be answered. A name that matched a rule decides for its
    // addresses, and the rest are left to the ranges.
    private bool ApplyAnswer(string name, RouteVerdict verdict, byte[] answer)
    {
        foreach (var address in DnsWire.ReadAddresses(answer, answer.Length))
        {
            if (address.AddressFamily != AddressFamily.InterNetwork
                || !GeoIpRanges.TryToNumeric(address, out var value))
            {
                continue;
            }

            if (_routes.Classify(value) == RouteVerdict.Block)
            {
                _log.Route($"{name} -> {address} blocked");
                return true;
            }

            if (verdict == RouteVerdict.None)
            {
                _routes.Note(value);
            }
            else
            {
                _routes.Note(value, verdict);
            }

            _log.Route($"{name} -> {address} {verdict.ToString().ToLowerInvariant()}");
        }

        return false;
    }

    // Direct wins over proxy on an overlap, and a blocked name is refused before either.
    private RouteVerdict Decide(string name)
    {
        if (_blockDomains.IsTunneled(name))
        {
            return RouteVerdict.Block;
        }

        if (_directDomains.IsTunneled(name))
        {
            return RouteVerdict.Direct;
        }

        return _proxyDomains.IsTunneled(name) ? RouteVerdict.Proxy : RouteVerdict.None;
    }

    // Asks each resolver in turn and returns the first answer.
    private async Task<byte[]?> AskAsync(IReadOnlyList<IPAddress> servers, byte[] query, int length, bool overTcp, CancellationToken ct)
    {
        foreach (var server in servers)
        {
            var answer = overTcp
                ? await AskOverTcpAsync(server, query, length, ct).ConfigureAwait(false)
                : await AskOverUdpAsync(server, query, length, ct).ConfigureAwait(false);
            if (answer is not null)
            {
                return answer;
            }
        }

        return null;
    }

    private async Task<byte[]?> AskOverUdpAsync(IPAddress server, byte[] query, int length, CancellationToken ct)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            await socket.ConnectAsync(new IPEndPoint(server, Port), ct).ConfigureAwait(false);
            await socket.SendAsync(query.AsMemory(0, length), SocketFlags.None, ct).ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(UpstreamTimeoutMs);
            var buffer = new byte[BufferSize];
            var read = await socket.ReceiveAsync(buffer, SocketFlags.None, timeout.Token).ConfigureAwait(false);
            return read <= 0 ? null : buffer[..read];
        }
        catch (OperationCanceledException)
        {
            _log.Warn("dns", $"resolver {server} did not answer in time");
            return null;
        }
        catch (SocketException ex)
        {
            _log.Warn("dns", $"resolver {server} failed: {ex.Message}");
            return null;
        }
    }

    private async Task<byte[]?> AskOverTcpAsync(IPAddress server, byte[] query, int length, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(UpstreamTimeoutMs);
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(new IPEndPoint(server, Port), timeout.Token).ConfigureAwait(false);
            await WriteFramedAsync(socket, length == query.Length ? query : query[..length], timeout.Token).ConfigureAwait(false);
            return await ReadFramedAsync(socket, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _log.Warn("dns", $"resolver {server} did not answer over TCP in time");
            return null;
        }
        catch (SocketException ex)
        {
            _log.Warn("dns", $"resolver {server} failed over TCP: {ex.Message}");
            return null;
        }
    }

    // Reads one length-prefixed message; null when the peer closed the connection.
    private static async Task<byte[]?> ReadFramedAsync(Socket socket, CancellationToken ct)
    {
        var header = new byte[2];
        if (!await ReadExactAsync(socket, header, ct).ConfigureAwait(false))
        {
            return null;
        }

        var length = (header[0] << 8) | header[1];
        if (length is 0 or > MaxMessageLength)
        {
            return null;
        }

        var message = new byte[length];
        return await ReadExactAsync(socket, message, ct).ConfigureAwait(false) ? message : null;
    }

    private static async Task<bool> ReadExactAsync(Socket socket, byte[] buffer, CancellationToken ct)
    {
        var filled = 0;
        while (filled < buffer.Length)
        {
            var read = await socket.ReceiveAsync(buffer.AsMemory(filled), SocketFlags.None, ct).ConfigureAwait(false);
            if (read == 0)
            {
                return false;
            }

            filled += read;
        }

        return true;
    }

    private static async Task WriteFramedAsync(Socket socket, byte[] message, CancellationToken ct)
    {
        var framed = new byte[message.Length + 2];
        framed[0] = (byte)(message.Length >> 8);
        framed[1] = (byte)message.Length;
        message.CopyTo(framed, 2);
        await socket.SendAsync(framed, SocketFlags.None, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();
        _udp?.Dispose();
        _tcp?.Dispose();
        _cts.Dispose();
    }
}
