using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Asks the DHCP server of the sharing what it hands the clients of the access point. The sharing answers an
/// offer, and that offer carries the resolver its clients are given; wedged, it answers nothing while the point
/// stands and the clients keep the addresses they had without a resolver, so their names go nowhere while bare
/// addresses still travel.
/// </summary>
internal static class HotspotDhcpProbe
{
    private const int ServerPort = 67;
    private const int ClientPort = 68;
    private const int WaitMs = 2000;
    private const int Offer = 2;
    private const int Size = 300;
    private static readonly byte[] Cookie = [99, 130, 83, 99];

    /// <summary>
    /// Whether the sharing offers its clients a resolver. True while nothing can be asked - a probe that cannot
    /// run answers nothing about the sharing, and a sharing is not restarted on a guess.
    /// </summary>
    public static async Task<bool> OffersResolverAsync(IPAddress scope, CancellationToken ct)
    {
        var octets = scope.GetAddressBytes();
        if (octets[0] != 192 || octets[1] != 168)
        {
            return true;
        }

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.EnableBroadcast = true;
            socket.Bind(new IPEndPoint(IPAddress.Any, ClientPort));
            var broadcast = new IPEndPoint(new IPAddress([octets[0], octets[1], octets[2], 255]), ServerPort);
            await socket.SendToAsync(Discover(), SocketFlags.None, broadcast, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return true;
        }

        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(WaitMs);
        var buffer = new byte[1500];
        var any = new IPEndPoint(IPAddress.Any, 0);
        try
        {
            while (!wait.IsCancellationRequested)
            {
                var received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, wait.Token).ConfigureAwait(false);
                if (received.ReceivedBytes > 240 && Named(buffer.AsSpan(0, received.ReceivedBytes)))
                {
                    return true;
                }
            }
        }
        catch (Exception)
        {
        }

        return false;
    }

    // Whether the answer is an offer and it names a resolver a client can use, which rules out the loopback of the
    // client itself.
    private static bool Named(ReadOnlySpan<byte> answer)
    {
        var offset = 240;
        var offered = false;
        var resolver = false;
        while (offset < answer.Length)
        {
            var code = answer[offset];
            if (code == 255)
            {
                break;
            }

            if (code == 0)
            {
                offset++;
                continue;
            }

            if (offset + 1 >= answer.Length)
            {
                break;
            }

            var length = answer[offset + 1];
            if (offset + 2 + length > answer.Length)
            {
                break;
            }

            var value = answer.Slice(offset + 2, length);
            if (code == 53 && length == 1 && value[0] == Offer)
            {
                offered = true;
            }
            else if (code == 6 && length >= 4)
            {
                for (var i = 0; i + 4 <= length; i += 4)
                {
                    var server = new IPAddress(value.Slice(i, 4));
                    if (!IPAddress.IsLoopback(server))
                    {
                        resolver = true;
                    }
                }
            }

            offset += 2 + length;
        }

        return offered && resolver;
    }

    // One discover of a client that asks for a lease and means to walk away from it, fresh every ask: the sharing
    // remembers a client it answered and hands the next offer to that lease's address alone, over the air, where
    // no socket of this machine hears it.
    private static byte[] Discover()
    {
        var client = new byte[6];
        Random.Shared.NextBytes(client.AsSpan(1));
        client[0] = 0x02;

        var packet = new byte[Size];
        packet[0] = 1;
        packet[1] = 1;
        packet[2] = 6;
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), (uint)Random.Shared.Next(1, int.MaxValue));
        packet[10] = 0x80;
        client.CopyTo(packet, 28);
        Cookie.CopyTo(packet, 236);
        packet[240] = 53;
        packet[241] = 1;
        packet[242] = 1;
        packet[243] = 61;
        packet[244] = 7;
        packet[245] = 0x01;
        client.CopyTo(packet, 246);
        packet[252] = 55;
        packet[253] = 3;
        packet[254] = 1;
        packet[255] = 3;
        packet[256] = 6;
        packet[257] = 255;
        return packet;
    }
}
