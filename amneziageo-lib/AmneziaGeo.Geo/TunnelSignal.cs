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
    public static SignalPlace? Of(ServerOffer? offer, string? configText, bool ipv6 = true)
    {
        if (offer is not { SignalPort: > 0 } || ConfigServices.Target(configText) is not { } keys)
        {
            return null;
        }

        var sources = offer.SignalSources()
            .Select(IPAddress.Parse)
            .Where(source => ipv6 || source.AddressFamily == AddressFamily.InterNetwork)
            .ToList();
        var at = TunnelInbound.Hosts(WgConfigEditor.GetAddresses(configText ?? string.Empty))
            .Select(IPAddress.Parse)
            .Where(address => sources.Exists(source => source.AddressFamily == address.AddressFamily))
            .ToList();
        var from = sources.Where(source => at.Exists(address => address.AddressFamily == source.AddressFamily)).ToList();

        return at.Count == 0 ? null : new SignalPlace(at, offer.SignalPort, from, keys.PrivateKey, keys.ServerKey);
    }
}
