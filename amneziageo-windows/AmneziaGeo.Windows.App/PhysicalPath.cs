using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Путь мимо туннеля: каким интерфейсом машина выходит наружу и как посадить на него сокет замера. Нужен, пока
/// туннель держит дефолт: замер чужого сервера обязан уйти физическим путём, иначе меряется не тот путь.
/// </summary>
internal static class PhysicalPath
{
    // Выбор исходящего интерфейса помимо таблицы маршрутов.
    private const int IpProtoIp = 0;
    private const int IpProtoIpv6 = 41;
    private const int UnicastIf = 31;

    // Даёт ли система сырой сокет ICMP: у службы права есть, у окна их нет.
    private static bool? _rawEcho;

    /// <summary>
    /// Индекс физического интерфейса, которым машина выходит наружу; null, когда такого нет. Берётся первый
    /// поднятый нетуннельный адаптер со шлюзом, тот же, с которого берётся шлюз для ближней ноги проверки.
    /// </summary>
    public static uint? InterfaceIndex()
    {
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up
                    || adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel
                    || RouteManager.IsTunnelAdapter(adapter))
                {
                    continue;
                }

                var properties = adapter.GetIPProperties();
                if (!HasGateway(properties))
                {
                    continue;
                }

                return (uint)properties.GetIPv4Properties().Index;
            }
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException or InvalidOperationException)
        {
        }

        return null;
    }

    /// <summary>
    /// Сажает сокет замера на физический интерфейс; null, когда выходить некуда или сырой сокет не дают.
    /// </summary>
    public static Func<Socket, bool>? Bypass()
    {
        if (InterfaceIndex() is not { } index || !RawEchoAllowed())
        {
            return null;
        }

        return socket => Apply(socket, index);
    }

    private static bool HasGateway(IPInterfaceProperties properties)
    {
        foreach (var gateway in properties.GatewayAddresses)
        {
            if (gateway.Address is { AddressFamily: AddressFamily.InterNetwork } address && !address.Equals(IPAddress.Any))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Apply(Socket socket, uint index)
    {
        try
        {
            var value = new byte[sizeof(uint)];
            if (socket.AddressFamily == AddressFamily.InterNetworkV6)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(value, index);
                socket.SetRawSocketOption(IpProtoIpv6, UnicastIf, value);
                return true;
            }

            // У IPv4 индекс кладётся в сетевом порядке байт, у IPv6 в обычном.
            BinaryPrimitives.WriteUInt32BigEndian(value, index);
            socket.SetRawSocketOption(IpProtoIp, UnicastIf, value);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            return false;
        }
    }

    private static bool RawEchoAllowed()
    {
        if (_rawEcho is { } known)
        {
            return known;
        }

        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.Icmp);
            _rawEcho = true;
        }
        catch (Exception ex) when (ex is SocketException or PlatformNotSupportedException)
        {
            _rawEcho = false;
        }

        return _rawEcho.Value;
    }
}
