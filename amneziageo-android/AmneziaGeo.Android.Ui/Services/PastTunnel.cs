using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Android.App;
using Android.Net;
using AmneziaGeo.Android.Engine;
using ParcelFileDescriptor = Android.OS.ParcelFileDescriptor;
using SocketType = System.Net.Sockets.SocketType;

namespace AmneziaGeo.Android.Ui.Services;

/// <summary>
/// Opens the connections of the head on the network under the tunnel: what the head asks the servers of the
/// configurations does not enter a tunnel that stands dialing.
/// </summary>
internal static class PastTunnel
{
    private const string Tag = "PastTunnel";

    // How long the network under the tunnel is given before the usual way is tried.
    private static readonly TimeSpan _lead = TimeSpan.FromSeconds(7);

    /// <summary>
    /// Connects to a host on the network under the tunnel; the usual way where the device sits on no such network,
    /// where the system binds no socket to it or where the host is not reached on it.
    /// </summary>
    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var point = context.DnsEndPoint;
        if (AndroidNetworks.Under(Application.Context) is { } under)
        {
            using var lead = CancellationTokenSource.CreateLinkedTokenSource(ct);
            lead.CancelAfter(_lead);
            try
            {
                return await DialAsync(point, under, lead.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is IOException or SocketException or OperationCanceledException)
            {
                global::Android.Util.Log.Info(Tag, $"{point.Host}:{point.Port} is not reached on the network under the tunnel: {ex.Message}");
            }
        }

        return await DialAsync(point, null, ct).ConfigureAwait(false);
    }

    // Connects to the first address of a host that takes the connection, on the network given or the usual way.
    private static async Task<Stream> DialAsync(DnsEndPoint point, Network? network, CancellationToken ct)
    {
        var failure = default(Exception);
        foreach (var address in await ResolveAsync(point.Host, network, ct).ConfigureAwait(false))
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                Bind(network, socket);
                await socket.ConnectAsync(new IPEndPoint(address, point.Port), ct).ConfigureAwait(false);

                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is SocketException or IOException)
            {
                socket.Dispose();
                failure = ex;
            }
            catch (Exception)
            {
                socket.Dispose();
                throw;
            }
        }

        throw failure ?? new SocketException((int)SocketError.HostNotFound);
    }

    // The addresses of a host as the network given resolves it, IPv4 first.
    private static async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, Network? network, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var literal))
        {
            return [literal];
        }

        if (network is null)
        {
            return Ordered(await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false));
        }

        try
        {
            var found = await Task.Run(() => network.GetAllByName(host), ct).WaitAsync(ct).ConfigureAwait(false);

            return Ordered((found ?? []).Select(one => one.GetAddress()).OfType<byte[]>().Select(bytes => new IPAddress(bytes)));
        }
        catch (Java.Lang.Exception ex)
        {
            throw new IOException($"{host} does not resolve on the network under the tunnel: {ex.Message}", ex);
        }
    }

    private static IReadOnlyList<IPAddress> Ordered(IEnumerable<IPAddress> addresses) =>
        [.. addresses.OrderBy(address => address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)];

    // Binds a socket to the network given, where one is given.
    private static void Bind(Network? network, Socket socket)
    {
        if (network is null)
        {
            return;
        }

        try
        {
            using var copy = ParcelFileDescriptor.FromFd(socket.Handle.ToInt32());
            if (copy?.FileDescriptor is { } descriptor)
            {
                network.BindSocket(descriptor);
            }
        }
        catch (Java.Lang.Exception ex)
        {
            throw new IOException($"the system binds no socket to the network under the tunnel: {ex.Message}", ex);
        }
    }
}
