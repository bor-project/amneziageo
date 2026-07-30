using System.Net;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Installs bypass routes and their kill-switch permits for the routing cache.
/// </summary>
internal sealed class RouteApplier(
    RouteManager routes,
    WindowsFirewall firewall,
    Func<(IPAddress? Gateway, uint InterfaceIndex)> hopProvider,
    bool killSwitch) : IRouteApplier
{
    private const long HopTtlMs = 30_000;

    private sealed record Hop(IPAddress? Gateway, uint InterfaceIndex, long Stamp);

    private Hop? _hop;

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
    /// Adds a host route through the physical gateway.
    /// </summary>
    public bool TryAddRoute(IPAddress address, out uint interfaceIndex)
    {
        var hop = ResolveHop();
        interfaceIndex = hop.InterfaceIndex;
        if (interfaceIndex == 0)
        {
            return false;
        }

        return routes.AddDirectHost(address, hop.Gateway, interfaceIndex);
    }

    /// <summary>
    /// Removes a host route.
    /// </summary>
    public void RemoveRoute(IPAddress address, uint interfaceIndex)
    {
        routes.RemoveDirectHost(address, interfaceIndex);
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
