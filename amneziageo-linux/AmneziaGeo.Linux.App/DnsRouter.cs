using System.Net;
using System.Net.Sockets;
using AmneziaGeo.Geo;

namespace AmneziaGeo.Linux.App;

/// <summary>
/// Answers the machine's lookups and decides per name, and per address the answer carries, whether the
/// destination goes through the tunnel or stays on the physical path.
/// </summary>
internal sealed class DnsRouter : IDisposable
{
    private const int Port = 53;
    private const int UpstreamTimeoutMs = 4000;
    private const int BufferSize = 4096;

    private readonly bool _split;
    private readonly DomainMatcher _proxyDomains;
    private readonly DomainMatcher _directDomains;
    private readonly DomainMatcher _blockDomains;
    private readonly IpRangeSet _proxyRanges;
    private readonly IpRangeSet _directRanges;
    private readonly IpRangeSet _blockRanges;
    private readonly IReadOnlyList<IPAddress> _tunnelResolvers;
    private readonly IReadOnlyList<IPAddress> _lanResolvers;
    private readonly LiveRoutes _routes;
    private readonly AgentLog _log;
    private readonly CancellationTokenSource _cts = new();
    private Socket? _socket;
    private bool _disposed;

    /// <summary>
    /// ctor
    /// </summary>
    public DnsRouter(TunnelRouting routing, IReadOnlyList<IPAddress> tunnelResolvers, IReadOnlyList<IPAddress> lanResolvers, LiveRoutes routes, AgentLog log)
    {
        _split = routing.Split;
        _proxyDomains = new DomainMatcher(routing.ProxyDomains);
        _directDomains = new DomainMatcher(routing.DirectDomains);
        _blockDomains = new DomainMatcher(routing.BlockDomains);
        _proxyRanges = new IpRangeSet(routing.ProxyRoutes);
        _directRanges = new IpRangeSet(routing.DirectRoutes);
        _blockRanges = new IpRangeSet(routing.BlockRoutes);
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
    /// Binds the listener and starts serving; false when the address is taken.
    /// </summary>
    public bool Start()
    {
        try
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(Listen, Port));
            _socket = socket;
        }
        catch (SocketException ex)
        {
            _log.Error("dns", $"binding {Listen}:{Port} failed", ex);
            return false;
        }

        _ = Task.Run(() => ServeAsync(_cts.Token));
        _log.Info("dns", $"name router on {Listen}:{Port}, {_proxyRanges.Count} proxy range(s), {_directRanges.Count} direct range(s)");
        return true;
    }

    // Receives queries and answers each one without holding up the next.
    private async Task ServeAsync(CancellationToken ct)
    {
        var socket = _socket;
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
                _ = Task.Run(() => HandleAsync(socket, buffer, received.ReceivedBytes, received.RemoteEndPoint, ct), ct);
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

    // Decides the query, forwards it to the resolver that fits, and routes what the answer resolved to.
    private async Task HandleAsync(Socket socket, byte[] query, int length, EndPoint client, CancellationToken ct)
    {
        try
        {
            if (!DnsWire.TryReadQuestion(query, length, out var name))
            {
                return;
            }

            var verdict = Decide(name);
            if (verdict == Verdict.Block)
            {
                await RefuseAsync(socket, query, length, client, ct).ConfigureAwait(false);
                return;
            }

            var upstream = verdict switch
            {
                Verdict.Tunnel => _tunnelResolvers,
                Verdict.Direct => _lanResolvers,
                _ => _split ? _lanResolvers : _tunnelResolvers,
            };

            var answer = await AskAsync(upstream, query, length, ct).ConfigureAwait(false);
            if (answer is null)
            {
                return;
            }

            if (await ApplyAnswerAsync(name, verdict, answer, ct).ConfigureAwait(false))
            {
                await RefuseAsync(socket, query, length, client, ct).ConfigureAwait(false);
                return;
            }

            await socket.SendToAsync(answer, SocketFlags.None, client, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.Error("dns", "answering a lookup failed", ex);
        }
    }

    // Installs the routes the answer earns; true when the destination is blocked and must not be answered.
    private async Task<bool> ApplyAnswerAsync(string name, Verdict verdict, byte[] answer, CancellationToken ct)
    {
        foreach (var address in DnsWire.ReadAddresses(answer, answer.Length))
        {
            if (address.AddressFamily != AddressFamily.InterNetwork)
            {
                continue;
            }

            if (_blockRanges.Contains(address))
            {
                return true;
            }

            if (_directRanges.Contains(address) || verdict == Verdict.Direct)
            {
                if (!_split)
                {
                    await _routes.BypassAsync(address, name, ct).ConfigureAwait(false);
                }

                continue;
            }

            if (_split && (verdict == Verdict.Tunnel || _proxyRanges.Contains(address)))
            {
                await _routes.TunnelAsync(address, name, ct).ConfigureAwait(false);
            }
        }

        return false;
    }

    // Direct wins over proxy on an overlap, and a blocked name is refused before either.
    private Verdict Decide(string name)
    {
        if (_blockDomains.IsTunneled(name))
        {
            return Verdict.Block;
        }

        if (_directDomains.IsTunneled(name))
        {
            return Verdict.Direct;
        }

        return _proxyDomains.IsTunneled(name) ? Verdict.Tunnel : Verdict.Default;
    }

    private async Task RefuseAsync(Socket socket, byte[] query, int length, EndPoint client, CancellationToken ct)
    {
        var refusal = DnsWire.BuildRefusal(query, length);
        if (refusal is not null)
        {
            await socket.SendToAsync(refusal, SocketFlags.None, client, ct).ConfigureAwait(false);
        }
    }

    // Asks each resolver in turn and returns the first answer.
    private async Task<byte[]?> AskAsync(IReadOnlyList<IPAddress> servers, byte[] query, int length, CancellationToken ct)
    {
        foreach (var server in servers)
        {
            var answer = await AskOneAsync(server, query, length, ct).ConfigureAwait(false);
            if (answer is not null)
            {
                return answer;
            }
        }

        return null;
    }

    private async Task<byte[]?> AskOneAsync(IPAddress server, byte[] query, int length, CancellationToken ct)
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

    private enum Verdict
    {
        Default,
        Tunnel,
        Direct,
        Block,
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
        _socket?.Dispose();
        _cts.Dispose();
    }
}
