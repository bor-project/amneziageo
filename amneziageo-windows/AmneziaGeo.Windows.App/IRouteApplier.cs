using System.Net;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// The system calls a routing verdict needs. Separated from the cache so the cache's own logic - precedence, idle
/// reclaim, limits - is testable without a tunnel.
/// </summary>
internal interface IRouteApplier
{
    /// <summary>
    /// Filter-set generation; 0 when no kill-switch is armed, so entries never look stale.
    /// </summary>
    int Generation { get; }

    /// <summary>
    /// Permits one host address through the physical path, reporting the filter ids and their generation.
    /// </summary>
    bool TryPermit(uint address, out ulong outId, out ulong inId, out int generation);

    /// <summary>
    /// Adds a host route out the physical path, reporting the interface it landed on.
    /// </summary>
    bool TryAddRoute(IPAddress address, out uint interfaceIndex);

    /// <summary>
    /// Removes a host route.
    /// </summary>
    void RemoveRoute(IPAddress address, uint interfaceIndex);

    /// <summary>
    /// Deletes host filters in one batch.
    /// </summary>
    void DeleteFilters(IReadOnlyList<(ulong Out, ulong In)> filters, int generation);
}
