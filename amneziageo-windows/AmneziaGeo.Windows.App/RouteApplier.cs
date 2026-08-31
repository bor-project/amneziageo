using System.Net;
using AmneziaGeo.Routing;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Installs bypass routes and their kill-switch permits for the routing cache.
/// </summary>
internal sealed class RouteApplier(
    RouteManager routes,
    WindowsFirewall firewall,
    UapiClient uapi,
    string tunnelName,
    string? peerPublicKey,
    Func<(IPAddress? Gateway, uint InterfaceIndex)> hopProvider,
    bool killSwitch,
    SynSentReset synReset) : IRouteApplier
{
    private const long HopTtlMs = 30_000;

    private sealed record Hop(IPAddress? Gateway, uint InterfaceIndex, long Stamp);

    private Hop? _hop;
    private uint? _tunnelIndex;

    /// <summary>
    /// Filter-set generation; 0 without a kill-switch, so entries never look stale.
    /// </summary>
    public int Generation => killSwitch ? firewall.Generation : 0;

    /// <summary>
    /// Permits one host address through the physical path; a no-op without a kill-switch.
    /// </summary>
    public bool TryPermit(uint address, out ulong outId, out ulong inId, out int generation)
    {
        if (!killSwitch)
        {
            outId = 0;
            inId = 0;
            generation = 0;
            return true;
        }

        return firewall.TryPermitHost(address, out outId, out inId, out generation);
    }

    /// <summary>
    /// Drops one host address at the highest weight, so a blocked destination loses to no permit.
    /// </summary>
    public bool TryDrop(uint address, out ulong outId, out ulong inId, out int generation)
    {
        return firewall.TryDropHost(address, out outId, out inId, out generation);
    }

    /// <summary>
    /// Adds a host route out the physical path; on-link for an address on the LAN, through the gateway otherwise.
    /// </summary>
    public bool TryAddRoute(IPAddress address, out uint interfaceIndex)
    {
        var hop = ResolveHop();
        interfaceIndex = hop.InterfaceIndex;
        if (interfaceIndex == 0)
        {
            return false;
        }

        // A /32 through the gateway outranks the on-link subnet route, so a LAN neighbour would be answered at the
        // router's MAC - and the router does not forward that back into the segment it came from.
        var gateway = routes.IsOnLocalSubnet(address) ? null : hop.Gateway;
        return routes.AddDirectHost(address, gateway, interfaceIndex);
    }

    /// <summary>
    /// Removes a host route.
    /// </summary>
    public void RemoveRoute(IPAddress address, uint interfaceIndex)
    {
        routes.RemoveDirectHost(address, interfaceIndex);
    }

    /// <summary>
    /// Routes one address into the tunnel and advertises it to the peer. The route goes in first: an advertised
    /// address with no route accepts inbound packets the host cannot answer.
    /// </summary>
    public bool TryTunnel(IPAddress address)
    {
        var index = TunnelIndex();
        if (index is null || peerPublicKey is null)
        {
            return false;
        }

        if (!routes.AddTunnelRoute(address, index.Value))
        {
            return false;
        }

        uapi.AddAllowedIps(tunnelName, peerPublicKey, [Cidr(address)]);
        // The attempt that discovered this address is still waiting, carrying the source address it was given
        // before the route existed. A route cannot move it, so it is aborted and the app opens a new one.
        synReset.Abort([address]);
        return true;
    }

    /// <summary>
    /// Withdraws tunnelled addresses: their routes go now, so the traffic falls back to the physical path, and the
    /// advertisements leave with the next batched request.
    /// </summary>
    public void RemoveTunnel(IReadOnlyCollection<IPAddress> addresses)
    {
        var index = TunnelIndex();
        if (addresses.Count == 0 || index is null)
        {
            return;
        }

        routes.RemoveTunnelRoutes(addresses, index.Value);
        if (peerPublicKey is null)
        {
            return;
        }

        var cidrs = new List<string>(addresses.Count);
        foreach (var address in addresses)
        {
            cidrs.Add(Cidr(address));
        }

        uapi.QueueRemoveAllowedIps(tunnelName, peerPublicKey, cidrs);
    }

    private static string Cidr(IPAddress address)
    {
        return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"{address}/128" : $"{address}/32";
    }

    private uint? TunnelIndex()
    {
        _tunnelIndex ??= routes.FindTunnelIndex(tunnelName);
        return _tunnelIndex;
    }

    /// <summary>
    /// Deletes host filters in one batch.
    /// </summary>
    public void DeleteFilters(IReadOnlyList<(ulong Out, ulong In)> filters, int generation)
    {
        if (!killSwitch || filters.Count == 0)
        {
            return;
        }

        firewall.DeleteHostFilters(filters, generation);
    }

    private Hop ResolveHop()
    {
        var now = Environment.TickCount64;
        var cached = _hop;
        if (cached is not null && cached.InterfaceIndex != 0 && now - cached.Stamp < HopTtlMs)
        {
            return cached;
        }

        var (gateway, interfaceIndex) = hopProvider();
        var fresh = new Hop(gateway, interfaceIndex, now);
        if (interfaceIndex != 0)
        {
            _hop = fresh;
        }

        return fresh;
    }
}
