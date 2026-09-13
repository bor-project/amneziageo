using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace AmneziaGeo.Geo;

/// <summary>
/// Private networks named by the configurations.
/// </summary>
public static class PrivateNetworks
{
    /// <summary>
    /// Ranges a tunnel carries, the ones left out for lying inside a network this machine stands in, and the networks
    /// of the machine lying inside a range carried.
    /// </summary>
    public sealed record LocalCut(IReadOnlyList<string> Carried, IReadOnlyList<string> Left, IReadOnlyList<string> Kept);

    /// <summary>
    /// Returns the private networks the configurations carry into their tunnels.
    /// </summary>
    public static IReadOnlyList<string> FromConfigs(IEnumerable<string> configs)
    {
        var found = new List<string>();
        foreach (var config in configs)
        {
            foreach (var network in FromConfig(config))
            {
                if (!found.Contains(network, StringComparer.OrdinalIgnoreCase))
                {
                    found.Add(network);
                }
            }
        }

        found.Sort(StringComparer.OrdinalIgnoreCase);
        return found;
    }

    /// <summary>
    /// Returns the private networks one configuration carries into its tunnel.
    /// </summary>
    public static IReadOnlyList<string> FromConfig(string config)
    {
        var found = new List<string>();
        foreach (var entry in WgConfigEditor.GetAllowedIps(config))
        {
            var network = entry.Trim();
            if (IsNetwork(network) && !found.Contains(network, StringComparer.OrdinalIgnoreCase))
            {
                found.Add(network);
            }
        }

        return found;
    }

    /// <summary>
    /// Returns the private networks one configuration reaches, less the ones this machine stands in itself.
    /// </summary>
    public static IReadOnlyList<string> ForTunnel(string config, IEnumerable<string> local)
    {
        var own = local as IReadOnlyCollection<string> ?? [.. local];
        var found = new List<string>(FromConfig(config));
        var addresses = TunnelInbound.Ranges(WgConfigEditor.GetAddresses(config), WgConfigEditor.GetAllowedIps(config), true);
        foreach (var network in addresses)
        {
            if (IsNetwork(network) && !found.Contains(network, StringComparer.OrdinalIgnoreCase))
            {
                found.Add(network);
            }
        }

        return [.. found.Where(network => !Overlaps(network, own))];
    }

    /// <summary>
    /// Returns every private network and private host one configuration reaches: the AllowedIPs of each peer and the
    /// network of each interface address.
    /// </summary>
    public static IReadOnlyList<string> Reachable(string config)
    {
        var allowed = WgConfigEditor.GetEveryAllowedIp(config);
        var found = new List<string>();
        foreach (var entry in allowed.Concat(TunnelInbound.Ranges(WgConfigEditor.GetEveryAddress(config), allowed, true)))
        {
            if (PrivateRange(entry) is { } range && !found.Contains(range, StringComparer.OrdinalIgnoreCase))
            {
                found.Add(range);
            }
        }

        return found;
    }

    /// <summary>
    /// Cuts the ranges a tunnel carries around the networks this machine stands in.
    /// </summary>
    public static LocalCut AroundLocal(IReadOnlyList<string> ranges, IReadOnlyList<string> local)
    {
        var own = new List<(string Network, IPAddress Address, int Prefix)>();
        foreach (var network in local)
        {
            if (TryRead(network, out var address, out var prefix))
            {
                own.Add((network, address!, prefix));
            }
        }

        if (own.Count == 0 || ranges.Count == 0)
        {
            return new LocalCut(ranges, [], []);
        }

        var carried = new List<string>(ranges.Count);
        var left = new List<string>();
        var kept = new List<string>();
        foreach (var range in ranges)
        {
            if (!TryRead(range, out var read, out var prefix))
            {
                carried.Add(range);
                continue;
            }

            var address = read!;
            if (LiesInside(address, prefix, own))
            {
                left.Add(range);
                continue;
            }

            carried.Add(range);
            foreach (var network in own)
            {
                if (prefix < network.Prefix
                    && address.AddressFamily == network.Address.AddressFamily
                    && SamePrefix(address, network.Address, prefix)
                    && !kept.Contains(network.Network, StringComparer.Ordinal))
                {
                    kept.Add(network.Network);
                }
            }
        }

        return new LocalCut(carried, left, kept);
    }

    /// <summary>
    /// Returns the networks this machine stands in itself.
    /// </summary>
    public static IReadOnlyList<string> Local()
    {
        return Local(string.Empty);
    }

    /// <summary>
    /// Lists the IPv4 networks of the interfaces that are up, past the named one.
    /// </summary>
    public static IReadOnlyList<string> Local(string except)
    {
        var found = new List<string>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up
                || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback
                || string.Equals(nic.Name, except, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var address in nic.GetIPProperties().UnicastAddresses)
            {
                var entry = $"{address.Address}/{address.PrefixLength}";
                if (address.Address.AddressFamily == AddressFamily.InterNetwork
                    && address.PrefixLength > 0
                    && !found.Contains(entry, StringComparer.OrdinalIgnoreCase))
                {
                    found.Add(entry);
                }
            }
        }

        return found;
    }

    /// <summary>
    /// Whether a network shares an address with any of the others.
    /// </summary>
    public static bool Overlaps(string network, IEnumerable<string> others)
    {
        if (!TryRead(network, out var address, out var prefix))
        {
            return false;
        }

        foreach (var other in others)
        {
            if (TryRead(other, out var theirs, out var theirPrefix)
                && address!.AddressFamily == theirs!.AddressFamily
                && SamePrefix(address, theirs, Math.Min(prefix, theirPrefix)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Tells a private network from a host address, a public range and the whole internet.
    /// </summary>
    public static bool IsNetwork(string entry)
    {
        var slash = entry.IndexOf('/');
        if (slash <= 0
            || !IPAddress.TryParse(entry[..slash], out var address)
            || !int.TryParse(entry[(slash + 1)..], out var prefix))
        {
            return false;
        }

        var full = address.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;
        return prefix > 0 && prefix < full && IsPrivate(address);
    }

    // Whether a range lies inside one of the networks.
    private static bool LiesInside(IPAddress address, int prefix, List<(string Network, IPAddress Address, int Prefix)> networks)
    {
        foreach (var network in networks)
        {
            if (prefix >= network.Prefix
                && address.AddressFamily == network.Address.AddressFamily
                && SamePrefix(address, network.Address, network.Prefix))
            {
                return true;
            }
        }

        return false;
    }

    // Restates a private network or a private host as the range it covers.
    private static string? PrivateRange(string entry)
    {
        var text = entry.Trim();
        var slash = text.IndexOf('/');
        if (!IPAddress.TryParse(slash < 0 ? text : text[..slash], out var address) || !IsPrivate(address))
        {
            return null;
        }

        var full = address.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;
        var prefix = slash < 0 ? full : int.TryParse(text[(slash + 1)..], out var parsed) ? parsed : -1;
        return prefix > 0 && prefix <= full ? $"{Masked(address, prefix)}/{prefix}" : null;
    }

    // Masks an address down to its network.
    private static IPAddress Masked(IPAddress address, int prefix)
    {
        var bytes = address.GetAddressBytes();
        for (var at = 0; at < bytes.Length; at++)
        {
            var bits = prefix - (at * 8);
            bytes[at] = bits >= 8 ? bytes[at] : bits <= 0 ? (byte)0 : (byte)(bytes[at] & (0xFF << (8 - bits)));
        }

        return new IPAddress(bytes);
    }

    // Reads a network into the address it starts at and the length of its prefix.
    private static bool TryRead(string entry, out IPAddress? address, out int prefix)
    {
        address = null;
        prefix = 0;
        var slash = entry.IndexOf('/');
        return slash > 0
            && IPAddress.TryParse(entry[..slash], out address)
            && int.TryParse(entry[(slash + 1)..], out prefix);
    }

    // Whether two addresses share their first bits.
    private static bool SamePrefix(IPAddress left, IPAddress right, int bits)
    {
        var first = left.GetAddressBytes();
        var second = right.GetAddressBytes();
        for (var at = 0; at < first.Length && bits > 0; at++, bits -= 8)
        {
            var mask = bits >= 8 ? 0xFF : 0xFF << (8 - bits) & 0xFF;
            if ((first[at] & mask) != (second[at] & mask))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsPrivate(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return (bytes[0] & 0xFE) == 0xFC;
        }

        return bytes[0] switch
        {
            10 => true,
            172 => bytes[1] >= 16 && bytes[1] <= 31,
            192 => bytes[1] == 168,
            _ => false,
        };
    }
}
