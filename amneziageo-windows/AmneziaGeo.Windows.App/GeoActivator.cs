using System.Net;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Routes a resolved IP through the tunnel by updating both the WG device and the OS route table.
/// </summary>
internal sealed class GeoActivator(string tunnelName, string peerPublicKey, uint interfaceIndex)
{
    /// <summary>
    /// Adds the IP to the peer's allowed IPs and to the tunnel route table.
    /// </summary>
    public bool TunnelIp(IPAddress ip)
    {
        var cidr = $"{ip}/32";
        var allowed = UapiClient.AddAllowedIp(tunnelName, peerPublicKey, cidr);
        var routed = RouteManager.AddTunnelRoute(ip, interfaceIndex);
        return allowed && routed;
    }
}
