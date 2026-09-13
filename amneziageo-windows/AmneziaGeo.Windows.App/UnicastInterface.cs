using System.Net.Sockets;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Sends what a socket carries out of one interface, whatever the routing table would pick.
/// </summary>
internal static class UnicastInterface
{
    // IP_UNICAST_IF; the option takes the index in network order.
    private const SocketOptionName Option = (SocketOptionName)31;

    /// <summary>
    /// Pins the socket to the interface with this index.
    /// </summary>
    public static void Pin(Socket socket, uint index)
    {
        socket.SetSocketOption(SocketOptionLevel.IP, Option, (int)Network(index));
    }

    // The order the option takes the index in.
    private static uint Network(uint index) =>
        ((index & 0xFF) << 24) | (((index >> 8) & 0xFF) << 16) | (((index >> 16) & 0xFF) << 8) | ((index >> 24) & 0xFF);
}
