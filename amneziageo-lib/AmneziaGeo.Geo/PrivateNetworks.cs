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
            foreach (var entry in WgConfigEditor.GetAllowedIps(config))
            {
                var network = entry.Trim();
                if (IsNetwork(network) && !found.Contains(network, StringComparer.OrdinalIgnoreCase))
                {
                    found.Add(network);
                }
            }
        }

        found.Sort(StringComparer.OrdinalIgnoreCase);
        return found;
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
