using System.Net;
using System.Net.Sockets;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Routes a resolved IP through the tunnel by updating both the WG device and the OS route table.
/// </summary>
internal sealed class GeoActivator(UapiClient uapi, RouteManager routes)
{
    /// <summary>
    /// Adds the IP to the peer's allowed IPs and to the tunnel route table.
    /// </summary>
    public bool TunnelIp(string tunnelName, string peerPublicKey, uint interfaceIndex, IPAddress ip)
    {
        var prefix = ip.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;
        var cidr = $"{ip}/{prefix}";
        var allowed = uapi.AddAllowedIp(tunnelName, peerPublicKey, cidr);
        var routed = routes.AddTunnelRoute(ip, interfaceIndex);
        return allowed && routed;
    }

    /// <summary>
    /// Removes the tunnel route for an IP that is no longer current.
    /// </summary>
    public void UntunnelIp(uint interfaceIndex, IPAddress ip)
    {
        routes.RemoveTunnelRoute(ip, interfaceIndex);
    }
}
