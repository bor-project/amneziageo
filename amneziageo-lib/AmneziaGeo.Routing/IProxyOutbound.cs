using System.Net;
using System.Net.Sockets;

namespace AmneziaGeo.Routing;

/// <summary>
/// How a proxied destination ended.
/// </summary>
public enum ProxyOutcome
{
    /// <summary>
    /// The destination is open.
    /// </summary>
    Ok,

    /// <summary>
    /// A rule refuses the destination.
    /// </summary>
    Blocked,

    /// <summary>
    /// The destination did not answer.
    /// </summary>
    Failed,
}

/// <summary>
/// Open destination the proxy passes bytes through.
/// </summary>
public interface IProxyLink : IDisposable
{
    /// <summary>
    /// Socket the destination is reached on.
    /// </summary>
    Socket Socket { get; }

    /// <summary>
    /// Adds payload carried in either direction.
    /// </summary>
    void Count(int bytes);
}

/// <summary>
/// Where the proxy sends what a client asks for. The platform decides: on a desktop the routing table carries
/// the socket, on Android the verdict of the routing rules does.
/// </summary>
public interface IProxyOutbound
{
    /// <summary>
    /// Opens one destination.
    /// </summary>
    Task<(IProxyLink? Link, ProxyOutcome Outcome)> ConnectAsync(string host, int port, CancellationToken ct);
}

/// <summary>
/// Destination opened as any other socket of this machine: the system routing table says whether it leaves
/// through the tunnel.
/// </summary>
public sealed class DirectProxyOutbound : IProxyOutbound
{
    private const int ConnectTimeoutMs = 8000;

    /// <inheritdoc/>
    public async Task<(IProxyLink? Link, ProxyOutcome Outcome)> ConnectAsync(string host, int port, CancellationToken ct)
    {
        var addresses = await ResolveAsync(host, ct).ConfigureAwait(false);
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ConnectTimeoutMs);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, port), timeout.Token).ConfigureAwait(false);
                return (new DirectProxyLink(socket), ProxyOutcome.Ok);
            }
            catch (Exception)
            {
                socket.Dispose();
            }
        }

        return (null, ProxyOutcome.Failed);
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

    /// <summary>
    /// Plain socket to the destination; nothing counts what it carries beyond the proxy's own totals.
    /// </summary>
    private sealed class DirectProxyLink(Socket socket) : IProxyLink
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
