using System.Net.Sockets;
using System.Text;

namespace AmneziaGeo.Linux.App;

/// <summary>
/// Binds a socket to a network device.
/// </summary>
internal static class DeviceSocket
{
    private const int SolSocket = 1;
    private const int SoBindToDevice = 25;

    /// <summary>
    /// Binds the socket to the device; false when the system refuses.
    /// </summary>
    public static bool Bind(Socket socket, string device)
    {
        try
        {
            socket.SetRawSocketOption(SolSocket, SoBindToDevice, Encoding.ASCII.GetBytes(device + "\0"));
            return true;
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            return false;
        }
    }
}
