using System.Net;
using System.Net.Sockets;

namespace AmneziaGeo.Geo;

/// <summary>
/// Private networks named by the configurations.
/// </summary>
public static class PrivateNetworks
{
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
