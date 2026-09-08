using System.Net;
using System.Net.Sockets;
using AmneziaGeo.Routing;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Where a session handed over by the gateway goes. The pair it was opened for names the program behind it, so a
/// rule on that program decides the whole session, and nothing of another program goes with it. An address rule
/// outranks the program: a range the rules named is answered before the owner is asked at all. The way out is
/// pinned to an interface rather than left to the routing table, or a session meant to leave the tunnel would come
/// back into the adapter it was terminated on.
/// </summary>
internal sealed class GatewayProxyOutbound(
    Func<uint, bool?>? owner,
    bool unknownRidesTunnel,
    GeoIpRanges proxy,
    GeoIpRanges direct,
    GeoIpRanges block,
    Func<uint> tunnelInterface,
    Func<uint> physicalInterface,
    ILogger logger) : IProxyOutbound, IDatagramOutbound
{
    private const int ConnectTimeoutMs = 8000;
    // IP_UNICAST_IF; the option takes the index in network order.
    private const SocketOptionName UnicastInterface = (SocketOptionName)31;

    /// <inheritdoc/>
    public Task<(IProxyLink? Link, ProxyOutcome Outcome)> ConnectAsync(string host, int port, CancellationToken ct) =>
        ConnectAsync(host, port, null, ct);

    /// <inheritdoc/>
    public async Task<(IProxyLink? Link, ProxyOutcome Outcome)> ConnectAsync(string host, int port, IPEndPoint? source, CancellationToken ct)
    {
        var addresses = await ResolveAsync(host, ct).ConfigureAwait(false);
        foreach (var address in addresses)
        {
            if (address.AddressFamily != AddressFamily.InterNetwork)
            {
                continue;
            }

            var numeric = Numeric(address);
            if (block.Contains(numeric))
            {
                return (null, ProxyOutcome.Blocked);
            }

            var tunnelled = Tunnelled(numeric, source, new IPEndPoint(address, port));
            var link = await OpenAsync(address, port, tunnelled, ct).ConfigureAwait(false);
            if (link is not null)
            {
                return (link, ProxyOutcome.Ok);
            }
        }

        return (null, ProxyOutcome.Failed);
    }

    // Whether this destination rides the tunnel: an address rule answers first, and only a destination no rule
    // named is decided by the program that opened the session.
    private bool Tunnelled(uint address, IPEndPoint? source, IPEndPoint destination)
    {
        if (proxy.Contains(address))
        {
            return true;
        }

        if (direct.Contains(address) || source is null || owner is null)
        {
            return false;
        }

        var pid = TcpTableProbe.OwnerOf(source, destination);
        var carried = Side(pid);
        logger.LogDebug("the session {Source} to {Destination} belongs to pid {Pid} and {Verdict}",
            source, destination, pid, carried ? "rides the tunnel" : "leaves past it");
        return carried;
    }

    // Opens the destination on the interface the verdict names.
    private async Task<IProxyLink?> OpenAsync(IPAddress address, int port, bool tunnelled, CancellationToken ct)
    {
        var index = tunnelled ? tunnelInterface() : physicalInterface();
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            if (index > 0)
            {
                socket.SetSocketOption(SocketOptionLevel.IP, UnicastInterface, (int)Network(index));
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ConnectTimeoutMs);
            await socket.ConnectAsync(new IPEndPoint(address, port), timeout.Token).ConfigureAwait(false);
            return new GatewayProxyLink(socket);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "the session to {Address}:{Port} did not open on interface {Index}", address, port, index);
            socket.Dispose();
            return null;
        }
    }

    /// <inheritdoc/>
    public Socket? Open(IPEndPoint? source, IPEndPoint destination)
    {
        if (destination.AddressFamily != AddressFamily.InterNetwork)
        {
            return null;
        }

        var numeric = Numeric(destination.Address);
        if (block.Contains(numeric))
        {
            return null;
        }

        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            var index = Carried(numeric, source) ? tunnelInterface() : physicalInterface();
            if (index > 0)
            {
                socket.SetSocketOption(SocketOptionLevel.IP, UnicastInterface, (int)Network(index));
            }

            return socket;
        }
        catch (SocketException ex)
        {
            logger.LogDebug(ex, "the flow to {Destination} did not open", destination);
            socket.Dispose();
            return null;
        }
    }

    // Whether a flow rides the tunnel: an address rule answers first, then the program that holds the socket.
    private bool Carried(uint address, IPEndPoint? source)
    {
        if (proxy.Contains(address))
        {
            return true;
        }

        if (direct.Contains(address) || source is null || owner is null)
        {
            return false;
        }

        var pid = TcpTableProbe.OwnerOfDatagram(source);
        var carried = Side(pid);
        logger.LogDebug("the flow from {Source} belongs to pid {Pid} and {Verdict}", source, pid,
            carried ? "rides the tunnel" : "leaves past it");
        return carried;
    }

    // The side a pid takes: what the rules say about its program, or the policy while nothing names it. A program
    // that lived for milliseconds is gone before its first datagram is asked about.
    private bool Side(uint pid)
    {
        if (owner!(pid) is { } known)
        {
            return known;
        }

        logger.LogDebug("nothing names the program behind pid {Pid}, so its traffic {Verdict}", pid,
            unknownRidesTunnel ? "rides the tunnel" : "leaves past it");
        return unknownRidesTunnel;
    }

    private static async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var literal))
        {
            return [literal];
        }

        try
        {
            return await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return [];
        }
    }

    // The order the option takes the index in.
    private static uint Network(uint index) =>
        ((index & 0xFF) << 24) | (((index >> 8) & 0xFF) << 16) | (((index >> 16) & 0xFF) << 8) | ((index >> 24) & 0xFF);

    private static uint Numeric(IPAddress address)
    {
        var octets = address.GetAddressBytes();
        return ((uint)octets[0] << 24) | ((uint)octets[1] << 16) | ((uint)octets[2] << 8) | octets[3];
    }

    /// <summary>
    /// Socket the session runs on; the proxy counts what it carries on its own.
    /// </summary>
    private sealed class GatewayProxyLink(Socket socket) : IProxyLink
    {
        /// <inheritdoc/>
        public Socket Socket { get; } = socket;

        /// <inheritdoc/>
        public void Count(int bytes)
        {
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            Socket.Dispose();
        }
    }
}
