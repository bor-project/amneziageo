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
    // IPv4 header 20, UDP header 8.
    private const int UdpCarrierBytes = 28;

    // What the same datagram costs inside a websocket instead: IPv4 header 20, TCP header 20 with 12 bytes of
    // options, TLS record 22, websocket frame 4.
    private const int WebSocketCarrierBytes = 78;

    // AmneziaWG data header 16, Poly1305 tag 16.
    private const int TunnelBytes = 32;

    // Room kept for the random trailer a profile appends to a data packet.
    private const int TrailerBytes = 16;

    /// <summary>
    /// Largest inner packet the link carries on this profile, capped by what the config declares.
    /// </summary>
    public static int Ceiling(string config, int linkMtu = MtuModes.MaxMtu, bool webSocket = false)
    {
        // Only what grows a data packet counts: the junk packets and the header keys leave its size alone.
        var carrier = webSocket ? WebSocketCarrierBytes : UdpCarrierBytes;
        var room = Math.Min(linkMtu, MtuModes.MaxMtu) - carrier - TunnelBytes - Padding(config);
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
    /// the largest the link, the carrier and the profile carry for auto.
    /// </summary>
    public static int Resolve(MtuMode mode, int stored, string config, int linkMtu = MtuModes.MaxMtu, bool webSocket = false)
    {
        var declared = WgConfigEditor.GetMtu(config);
        return mode switch
        {
            MtuMode.Custom => stored > 0 ? stored : Declared(declared),
            MtuMode.Config => Declared(declared),
            _ => Ceiling(config, linkMtu, webSocket),
        };
    }

    /// <summary>
    /// MTU for this config, asking the link how much it carries when the mode is auto. Nothing leaves the device:
    /// the link is read off the interface a route to the endpoint goes through.
    /// </summary>
    public static int ResolveForLink(MtuMode mode, int stored, string config, bool webSocket = false)
        => ForLink(mode, stored, config, webSocket, LinkMtu.Towards);

    /// <inheritdoc cref="ResolveForLink(MtuMode, int, string, bool)"/>
    public static int ResolveForLink(ConfigTransport? transport, string config)
        => ResolveForLink(transport?.MtuMode ?? MtuMode.Auto, transport?.Mtu ?? 0, config, transport?.UseWebSocket ?? false);

    /// <summary>
    /// The same size built from what the link towards the endpoint was last seen to carry. Nothing is looked up, so
    /// a status snapshot names the size without waiting on a name.
    /// </summary>
    public static int ResolveForLearnedLink(MtuMode mode, int stored, string config, bool webSocket = false)
        => ForLink(mode, stored, config, webSocket, LinkMtu.Learned);

    /// <inheritdoc cref="ResolveForLearnedLink(MtuMode, int, string, bool)"/>
    public static int ResolveForLearnedLink(ConfigTransport? transport, string config)
        => ResolveForLearnedLink(transport?.MtuMode ?? MtuMode.Auto, transport?.Mtu ?? 0, config, transport?.UseWebSocket ?? false);

    // Auto is the only mode the link has a say in; a link that cannot be told counts as the largest there can be.
    private static int ForLink(MtuMode mode, int stored, string config, bool webSocket, Func<string, int> link)
    {
        if (mode != MtuMode.Auto)
        {
            return Resolve(mode, stored, config, MtuModes.MaxMtu, webSocket);
        }

        var mtu = link(WgConfigEditor.GetEndpoint(config) ?? string.Empty);
        return Resolve(mode, stored, config, mtu > 0 ? mtu : MtuModes.MaxMtu, webSocket);
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
