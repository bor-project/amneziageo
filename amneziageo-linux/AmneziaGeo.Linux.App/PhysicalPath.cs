using System.Net.Sockets;
using System.Text;

namespace AmneziaGeo.Linux.App;

/// <summary>
/// Путь мимо туннеля: сажает сокет замера на физическое устройство, пока туннель держит дефолт. Иначе замер
/// чужого сервера уедет в туннель и померит не тот путь.
/// </summary>
internal static class PhysicalPath
{
    private const int SolSocket = 1;
    private const int SoBindToDevice = 25;

    /// <summary>
    /// Сажает сокет на устройство; null, когда устройства нет или система привязку не даёт.
    /// </summary>
    public static Func<Socket, bool>? Bypass(string? device)
    {
        if (string.IsNullOrEmpty(device) || !Allowed(device))
        {
            return null;
        }

        return socket => Apply(socket, device);
    }

    private static bool Apply(Socket socket, string device)
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

    // Привязка к устройству требует прав; без них замер мимо туннеля не уйдёт, и лучше знать это заранее.
    private static bool Allowed(string device)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            return Apply(socket, device);
        }
        catch (Exception ex) when (ex is SocketException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
