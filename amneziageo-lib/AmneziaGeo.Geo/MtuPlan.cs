using System.Globalization;
using AmneziaGeo.Decl;

namespace AmneziaGeo.Geo;

/// <summary>
/// Picks the MTU a tunnel comes up with. A packet leaves the device as an inner packet inside an AmneziaWG
/// datagram, so what the link carries has to hold the headers and, on a 3.1 profile, the padding the profile adds;
/// what is left over is the largest inner packet that never fragments.
/// </summary>
public static class MtuPlan
{
    // IPv4 header 20, UDP header 8, AmneziaWG data header 16, Poly1305 tag 16.
    private const int HeaderBytes = 60;

    // Room kept for the random trailer a profile appends to a data packet.
    private const int TrailerBytes = 16;

    /// <summary>
    /// Largest inner packet the link carries on this profile, capped by what the config declares.
    /// </summary>
    public static int Ceiling(string config, int linkMtu = MtuModes.MaxMtu)
    {
        // Only what grows a data packet counts: the junk packets and the header keys leave its size alone.
        var room = Math.Min(linkMtu, MtuModes.MaxMtu) - HeaderBytes - Padding(config);
        if (IsOn(config, "RandomTrailers"))
        {
            room -= TrailerBytes;
        }

        var declared = WgConfigEditor.GetMtu(config);
        if (declared > 0)
        {
            room = Math.Min(room, declared);
        }

        return Math.Clamp(room, MtuModes.MinMtu, MtuModes.MaxMtu);
    }

    /// <summary>
    /// MTU the tunnel comes up with under this mode: the stored size for custom, the declared one for config, and
    /// the largest the link and the profile carry for auto.
    /// </summary>
    public static int Resolve(MtuMode mode, int stored, string config, int linkMtu = MtuModes.MaxMtu)
    {
        var declared = WgConfigEditor.GetMtu(config);
        return mode switch
        {
            MtuMode.Custom => stored > 0 ? stored : Declared(declared),
            MtuMode.Config => Declared(declared),
            _ => Ceiling(config, linkMtu),
        };
    }

    /// <summary>
    /// MTU for this config, asking the link how much it carries when the mode is auto. Nothing leaves the device:
    /// the link is read off the interface a route to the endpoint goes through.
    /// </summary>
    public static int ResolveForLink(MtuMode mode, int stored, string config)
    {
        if (mode != MtuMode.Auto)
        {
            return Resolve(mode, stored, config);
        }

        var link = LinkMtu.Towards(WgConfigEditor.GetEndpoint(config) ?? string.Empty);
        return Resolve(mode, stored, config, link > 0 ? link : MtuModes.MaxMtu);
    }

    private static int Declared(int declared) => declared > 0 ? declared : WgConfigEditor.DefaultMtu;

    // Content the profile adds to a data packet; the value is a range and the upper end is what has to fit.
    private static int Padding(string config)
    {
        var value = Value(config, "ContentPaddingAddition");
        if (value.Length == 0)
        {
            return 0;
        }

        var end = value.Split('-')[^1].Trim();
        return int.TryParse(end, NumberStyles.Integer, CultureInfo.InvariantCulture, out var padding) && padding > 0
            ? padding
            : 0;
    }

    // A flag the engine reads as 1/0; a key written as off costs a packet nothing.
    private static bool IsOn(string config, string key) => Value(config, key).ToLowerInvariant() switch
    {
        "on" or "1" or "true" or "t" or "yes" => true,
        _ => false,
    };

    private static string Value(string config, string key)
    {
        foreach (var line in config.Split('\n'))
        {
            var trimmed = line.Trim();
            var equals = trimmed.IndexOf('=');
            if (equals <= 0 || !trimmed[..equals].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return trimmed[(equals + 1)..].Trim();
        }

        return string.Empty;
    }
}
