using System.Net;
using System.Net.Sockets;

namespace AmneziaGeo.Routing;

/// <summary>
/// Addresses no packet goes to through a tunnel: the machine itself (127.0.0.0/8, ::1), no address at all
/// (0.0.0.0/8, ::), a link of its own (169.254.0.0/16, fe80::/10), a group (224.0.0.0/4, ff00::/8) and the reserved
/// block with the broadcast (240.0.0.0/4). None of them earns a verdict, a route or a place among the ranges of the
/// engine. A private address is not one of them: a network behind the server is reached through the tunnel.
/// </summary>
public static class SpecialAddresses
{
    /// <summary>
    /// Whether a host-order IPv4 address is one of them.
    /// </summary>
    public static bool Holds(uint address)
    {
        var first = address >> 24;
        return first is 0 or 127 || first >= 224 || address >> 16 == 0xA9FE;
    }

    /// <summary>
    /// Whether an address of either family is one of them; an IPv4 address mapped into IPv6 counts as itself.
    /// </summary>
    public static bool Holds(IPAddress address)
    {
        var plain = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        if (GeoIpRanges.TryToNumeric(plain, out var value))
        {
            return Holds(value);
        }

        return plain.AddressFamily == AddressFamily.InterNetworkV6
            && (IPAddress.IsLoopback(plain) || plain.Equals(IPAddress.IPv6Any) || plain.IsIPv6LinkLocal || plain.IsIPv6Multicast);
    }
}
