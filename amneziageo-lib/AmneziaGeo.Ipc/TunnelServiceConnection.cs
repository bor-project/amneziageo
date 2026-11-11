using System.Net;
using System.Net.Sockets;

namespace AmneziaGeo.Ipc;

/// <summary>
/// Opens a service connection confined to a tunnel.
/// </summary>
public static class TunnelServiceConnection
{
    public static async ValueTask<Stream> ConnectAsync(DnsEndPoint point,
        Func<IPAddress, Socket, CancellationToken, ValueTask<bool>> confine, CancellationToken ct)
    {
        if (!IPAddress.TryParse(point.Host, out var address))
        {
            throw new IOException("The tunnel service requires an IP address.");
        }

        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            if (!await confine(address, socket, ct).ConfigureAwait(false))
            {
                throw new IOException("The tunnel service path is unavailable.");
            }

            await socket.ConnectAsync(new IPEndPoint(address, point.Port), ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
