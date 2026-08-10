using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace AmneziaGeo.Ipc;

/// <summary>
/// One echo and the time it took. Windows echoes through the OS helper, which needs no elevation; linux and android
/// take an unprivileged ICMP datagram socket, the runtime looking for a ping binary where android keeps none.
/// </summary>
public static class IcmpEcho
{
    /// <summary>
    /// Round trip in milliseconds, or -1 when nothing came back before the timeout.
    /// </summary>
    public static Task<int> RoundTripAsync(IPAddress address, int timeoutMs, CancellationToken ct)
    {
        return OperatingSystem.IsWindows()
            ? SystemAsync(address, timeoutMs)
            : SocketAsync(address, timeoutMs, ct);
    }

    private static async Task<int> SystemAsync(IPAddress address, int timeoutMs)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(address, timeoutMs).ConfigureAwait(false);
            return reply.Status == IPStatus.Success ? (int)reply.RoundtripTime : -1;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private static async Task<int> SocketAsync(IPAddress address, int timeoutMs, CancellationToken ct)
    {
        var v6 = address.AddressFamily == AddressFamily.InterNetworkV6;
        var socket = default(Socket);
        try
        {
            socket = new Socket(address.AddressFamily, SocketType.Dgram, v6 ? ProtocolType.IcmpV6 : ProtocolType.Icmp);
            socket.Connect(new IPEndPoint(address, 0));
        }
        catch (Exception)
        {
            // A kernel that hands out no ping socket leaves the OS helper as the only way to echo.
            socket?.Dispose();
            return await SystemAsync(address, timeoutMs).ConfigureAwait(false);
        }

        using (socket)
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            deadline.CancelAfter(timeoutMs);
            var reply = new byte[128];
            var clock = Stopwatch.StartNew();
            try
            {
                await socket.SendAsync(Request(v6), SocketFlags.None, deadline.Token).ConfigureAwait(false);
                var received = await socket.ReceiveAsync(reply, SocketFlags.None, deadline.Token).ConfigureAwait(false);
                return received > 0 ? (int)clock.ElapsedMilliseconds : -1;
            }
            catch (Exception)
            {
                return -1;
            }
        }
    }

    // An echo request: type, code, checksum, identifier, sequence, and a short payload. A ping socket rewrites the
    // identifier and the IPv6 checksum itself.
    private static byte[] Request(bool v6)
    {
        var packet = new byte[16];
        packet[0] = v6 ? (byte)128 : (byte)8;
        packet[4] = 0x41;
        packet[5] = 0x47;
        packet[7] = 1;
        for (var i = 8; i < packet.Length; i++)
        {
            packet[i] = (byte)i;
        }

        if (!v6)
        {
            var sum = Checksum(packet);
            packet[2] = (byte)(sum >> 8);
            packet[3] = (byte)sum;
        }

        return packet;
    }

    // The one's complement of the one's complement sum over the packet's 16-bit words.
    private static ushort Checksum(byte[] packet)
    {
        var sum = 0;
        for (var i = 0; i + 1 < packet.Length; i += 2)
        {
            sum += (packet[i] << 8) | packet[i + 1];
        }

        while ((sum >> 16) != 0)
        {
            sum = (sum & 0xFFFF) + (sum >> 16);
        }

        return (ushort)~sum;
    }
}
