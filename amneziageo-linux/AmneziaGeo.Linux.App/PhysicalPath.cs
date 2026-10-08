using System.Net.Sockets;

namespace AmneziaGeo.Linux.App;

/// <summary>
/// Путь мимо туннеля: сажает сокет замера на физическое устройство, пока туннель держит дефолт. Иначе замер
/// чужого сервера уедет в туннель и померит не тот путь.
/// </summary>
internal static class PhysicalPath
{
    /// <summary>
    /// Сажает сокет на устройство; null, когда устройства нет или система привязку не даёт.
    /// </summary>
    public static Func<Socket, bool>? Bypass(string? device)
    {
        if (string.IsNullOrEmpty(device) || !Allowed(device))
        {
            return null;
        }

        return socket => DeviceSocket.Bind(socket, device);
    }

    // Привязка к устройству требует прав; без них замер мимо туннеля не уйдёт, и лучше знать это заранее.
    private static bool Allowed(string device)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            return DeviceSocket.Bind(socket, device);
        }
        catch (Exception ex) when (ex is SocketException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
