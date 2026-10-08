using System.Net;
using System.Net.Sockets;
using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;

namespace AmneziaGeo.Geo;

/// <summary>
/// Works out where a configuration takes the signal of its server to disconnect.
/// </summary>
public static class TunnelSignal
{
    /// <summary>
    /// Returns where the device takes the signal under what its server offers: its own addresses inside the tunnel
    /// of a family the signal comes over, IPv4 alone for a tunnel that carries no IPv6; null when the server sends
    /// none or the text names no keys.
    /// </summary>
    public static SignalPlace? Of(ServerOffer? offer, string? configText, bool ipv6 = true) =>
        offer is { SignalPort: > 0 } ? Of(offer.SignalPort, offer.SignalSources(), configText, ipv6) : null;

    /// <summary>
    /// Returns where the device takes the signal that comes to the port from the addresses named; null when no
    /// address of the device answers them or the text names no keys.
    /// </summary>
    public static SignalPlace? Of(int port, IReadOnlyList<string> sources, string? configText, bool ipv6 = true)
    {
        ArgumentNullException.ThrowIfNull(sources);

        if (port <= 0 || ConfigServices.Target(configText) is not { } keys)
        {
            return null;
        }

        var named = new List<IPAddress>();
        foreach (var source in sources)
        {
            if (IPAddress.TryParse(source, out var address) && (ipv6 || address.AddressFamily == AddressFamily.InterNetwork))
            {
                named.Add(address);
            }
        }

        var at = TunnelInbound.Hosts(WgConfigEditor.GetAddresses(configText ?? string.Empty))
            .Select(IPAddress.Parse)
            .Where(address => named.Exists(source => source.AddressFamily == address.AddressFamily))
            .ToList();
        var from = named.Where(source => at.Exists(address => address.AddressFamily == source.AddressFamily)).ToList();

        return at.Count == 0 ? null : new SignalPlace(at, port, from, keys.PrivateKey, keys.ServerKey);
    }
}
