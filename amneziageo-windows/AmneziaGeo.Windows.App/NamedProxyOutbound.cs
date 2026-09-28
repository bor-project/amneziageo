using System.Net;
using System.Net.Sockets;
using AmneziaGeo.Routing;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Where the gateway opens a session. A name the rules send through the tunnel is looked up behind the tunnel and
/// opened there, whatever the system was answered for it; an address the system got in place of a real one is
/// reached on the physical adapter, so a session never comes back into the gateway; anything else is opened as any
/// other socket of this machine and the routing table carries it.
/// </summary>
internal sealed class NamedProxyOutbound(
    DnsProxy names,
    SubstitutedAddresses substituted,
    Func<uint?> tunnelInterface,
    Func<uint> physicalInterface,
    ILogger logger) : IProxyOutbound
{
    private const int ConnectTimeoutMs = 8000;

    /// <inheritdoc/>
    public async Task<(IProxyLink? Link, ProxyOutcome Outcome)> ConnectAsync(string host, int port, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var literal))
        {
            return await OpenAsync([literal], port, null, ct).ConfigureAwait(false);
        }

        var verdict = names.NameVerdict(host);
        if (verdict == RouteVerdict.Block)
        {
            return (null, ProxyOutcome.Blocked);
        }

        if (verdict == RouteVerdict.Proxy)
        {
            var carried = await names.TunnelAddressesAsync(host).ConfigureAwait(false);
            return await OpenAsync(carried, port, tunnelInterface(), ct).ConfigureAwait(false);
        }

        return await OpenAsync(await ResolveAsync(host, ct).ConfigureAwait(false), port, null, ct).ConfigureAwait(false);
    }

    // Opens the first address that answers, on the interface given or, without one, where the routing table sends it.
    private async Task<(IProxyLink? Link, ProxyOutcome Outcome)> OpenAsync(IReadOnlyList<IPAddress> addresses, int port, uint? pinned, CancellationToken ct)
    {
        foreach (var address in addresses)
        {
            var index = pinned ?? (substituted.Contains(address) ? physicalInterface() : 0);
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                if (index > 0 && address.AddressFamily == AddressFamily.InterNetwork)
                {
                    UnicastInterface.Pin(socket, index);
                }

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(ConnectTimeoutMs);
                await socket.ConnectAsync(new IPEndPoint(address, port), timeout.Token).ConfigureAwait(false);
                return (new NamedProxyLink(socket), ProxyOutcome.Ok);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "the session to {Address}:{Port} did not open on interface {Index}", address, port, index);
                socket.Dispose();
            }
        }

        return (null, ProxyOutcome.Failed);
    }

    private async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken ct)
    {
        try
        {
            return await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "{Host} was not looked up, so the session to it did not open", host);
            return [];
        }
    }

    /// <summary>
    /// Socket the session runs on; the proxy counts what it carries on its own.
    /// </summary>
    private sealed class NamedProxyLink(Socket socket) : IProxyLink
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
