using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace AmneziaGeo.Geo;

/// <summary>
/// Address ranges a configuration accepts traffic from when inbound access is on: the tunnel gateway alone, or
/// the whole tunnel network.
/// </summary>
public static class TunnelInbound
{
    // Prefix a bare interface address stands for, per family.
    private const int DefaultV4Prefix = 24;
    private const int DefaultV6Prefix = 120;

    /// <summary>
    /// Returns the ranges to advertise for the given interface addresses; wholeNetwork widens each one from the
    /// gateway to its whole network.
    /// </summary>
    public static IReadOnlyList<string> Ranges(IReadOnlyList<string> addresses, bool wholeNetwork)
    {
        var result = new List<string>();
        foreach (var entry in addresses)
        {
            var range = Range(entry, wholeNetwork);
            if (range is not null && !result.Contains(range))
            {
                result.Add(range);
            }
        }

        return result;
    }

    /// <summary>
    /// Returns the ranges to advertise, taking the prefix of each address from the AllowedIPs entry that holds it.
    /// </summary>
    public static IReadOnlyList<string> Ranges(IReadOnlyList<string> addresses, IReadOnlyList<string> allowedIps, bool wholeNetwork) =>
        Ranges([.. addresses.Select(entry => Covered(entry, allowedIps))], wholeNetwork);

    // Restates the address with the prefix of the narrowest network of the list that holds it.
    private static string Covered(string entry, IReadOnlyList<string> allowedIps)
    {
        var text = entry.Trim();
        var slash = text.IndexOf('/');
        var host = slash < 0 ? text : text[..slash];
        if (!IPAddress.TryParse(host, out var address))
        {
            return entry;
        }

        var best = 0;
        foreach (var candidate in allowedIps)
        {
            var mark = candidate.IndexOf('/');
            if (mark <= 0
                || !IPAddress.TryParse(candidate[..mark].Trim(), out var network)
                || !int.TryParse(candidate[(mark + 1)..].Trim(), out var prefix))
            {
                continue;
            }

            var full = network.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;
            if (network.AddressFamily != address.AddressFamily || prefix <= 0 || prefix >= full || prefix <= best)
            {
                continue;
            }

            if (Network(address, prefix).Equals(Network(network, prefix)))
            {
                best = prefix;
            }
        }

        return best > 0 ? $"{host}/{best}" : entry;
    }

    /// <summary>
    /// Returns the given interface addresses themselves, without the prefix length.
    /// </summary>
    public static IReadOnlyList<string> Hosts(IReadOnlyList<string> addresses)
    {
        var result = new List<string>();
        foreach (var entry in addresses)
        {
            var text = entry.Trim();
            var slash = text.IndexOf('/');
            if (!IPAddress.TryParse(slash < 0 ? text : text[..slash], out var address))
            {
                continue;
            }

            var host = address.ToString();
            if (!result.Contains(host))
            {
                result.Add(host);
            }
        }

        return result;
    }

    // Turns one interface address into the range traffic may arrive from.
    private static string? Range(string entry, bool wholeNetwork)
    {
        var text = entry.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        var slash = text.IndexOf('/');
        var host = slash < 0 ? text : text[..slash];
        if (!IPAddress.TryParse(host, out var address))
        {
            return null;
        }

        var v6 = address.AddressFamily == AddressFamily.InterNetworkV6;
        var full = v6 ? 128 : 32;
        var declared = slash < 0 || !int.TryParse(text[(slash + 1)..], out var parsed) || parsed < 0 || parsed > full
            ? full
            : parsed;
        var prefix = declared == full ? (v6 ? DefaultV6Prefix : DefaultV4Prefix) : declared;
        var network = Network(address, prefix);
        return wholeNetwork ? $"{network}/{prefix}" : $"{Next(network)}/{full}";
    }

    // Masks the address down to its network.
    private static IPAddress Network(IPAddress address, int prefix)
    {
        var bytes = address.GetAddressBytes();
        for (var i = 0; i < bytes.Length; i++)
        {
            var bits = prefix - (i * 8);
            bytes[i] = bits >= 8 ? bytes[i] : bits <= 0 ? (byte)0 : (byte)(bytes[i] & (0xFF << (8 - bits)));
        }

        return new IPAddress(bytes);
    }

    // The first host of a network, which is where the server sits.
    private static IPAddress Next(IPAddress network)
    {
        var bytes = network.GetAddressBytes();
        for (var i = bytes.Length - 1; i >= 0; i--)
        {
            if (++bytes[i] != 0)
            {
                break;
            }
        }

        return new IPAddress(bytes);
    }
}
