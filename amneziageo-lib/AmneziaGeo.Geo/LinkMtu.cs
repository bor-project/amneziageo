using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace AmneziaGeo.Geo;

/// <summary>
/// How large a packet the link towards a server carries. The interface a route to the endpoint leaves through is
/// found by connecting a datagram socket, which sends nothing, and its own MTU is what the first hop takes.
/// </summary>
public static class LinkMtu
{
    /// <summary>
    /// MTU of the interface a packet to this endpoint leaves through, or zero when it cannot be told.
    /// </summary>
    public static int Towards(string endpoint)
    {
        var address = LocalAddress(endpoint);
        return address is null ? 0 : MtuOf(address);
    }

    // The address a datagram socket takes when it is pointed at the endpoint; nothing leaves the device for it.
    private static IPAddress? LocalAddress(string endpoint)
    {
        var host = Host(endpoint, out var port);
        if (host.Length == 0)
        {
            return null;
        }

        try
        {
            if (!IPAddress.TryParse(host, out var target))
            {
                target = Array.Find(Dns.GetHostAddresses(host), candidate => candidate.AddressFamily == AddressFamily.InterNetwork);
                if (target is null)
                {
                    return null;
                }
            }

            using var socket = new Socket(target.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(new IPEndPoint(target, port));
            return (socket.LocalEndPoint as IPEndPoint)?.Address;
        }
        catch (SocketException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static int MtuOf(IPAddress address)
    {
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                var properties = adapter.GetIPProperties();
                foreach (var unicast in properties.UnicastAddresses)
                {
                    if (!unicast.Address.Equals(address))
                    {
                        continue;
                    }

                    var mtu = properties.GetIPv4Properties()?.Mtu ?? 0;
                    return mtu > 0 ? mtu : 0;
                }
            }
        }
        catch (NetworkInformationException)
        {
        }
        catch (PlatformNotSupportedException)
        {
        }

        return 0;
    }

    // Splits host:port; a bare host takes the AmneziaWG default port, which only steers the route lookup.
    private static string Host(string endpoint, out int port)
    {
        port = 51820;
        var value = endpoint?.Trim() ?? string.Empty;
        var colon = value.LastIndexOf(':');
        if (colon <= 0)
        {
            return value;
        }

        if (int.TryParse(value[(colon + 1)..], out var parsed) && parsed is > 0 and <= 65535)
        {
            port = parsed;
        }

        return value[..colon].Trim();
    }
}
