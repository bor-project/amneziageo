using System.Collections.Concurrent;
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
    // What each endpoint's link was last seen to carry; a status snapshot asks for every configuration it lists.
    private static readonly ConcurrentDictionary<string, int> Readings = new(StringComparer.OrdinalIgnoreCase);

    // Endpoints a background reading is already running for.
    private static readonly ConcurrentDictionary<string, byte> Asking = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// ctor
    /// </summary>
    static LinkMtu()
    {
        try
        {
            NetworkChange.NetworkAddressChanged += (_, _) => Readings.Clear();
        }
        catch (PlatformNotSupportedException)
        {
        }
    }

    /// <summary>
    /// MTU of the interface a packet to this endpoint leaves through, or zero when it cannot be told.
    /// </summary>
    public static int Towards(string endpoint)
    {
        var key = endpoint?.Trim() ?? string.Empty;
        var address = LocalAddress(key);
        var mtu = address is null ? 0 : MtuOf(address);
        Readings[key] = mtu;
        return mtu;
    }

    /// <summary>
    /// What the link towards this endpoint was last seen to carry, zero until it is known. Nothing is looked up
    /// here: a missing reading is taken in the background, and a network change drops what was kept.
    /// </summary>
    public static int Learned(string endpoint)
    {
        var key = endpoint?.Trim() ?? string.Empty;
        if (Readings.TryGetValue(key, out var mtu))
        {
            return mtu;
        }

        if (Asking.TryAdd(key, 0))
        {
            _ = Task.Run(() =>
            {
                try
                {
                    Towards(key);
                }
                finally
                {
                    Asking.TryRemove(key, out _);
                }
            });
        }

        return 0;
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
