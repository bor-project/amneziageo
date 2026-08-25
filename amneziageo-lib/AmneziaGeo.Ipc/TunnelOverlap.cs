namespace AmneziaGeo.Ipc;

/// <summary>
/// Whether two tunnels stand in the same addresses. Servers handing out one subnet - the default on every
/// self-hosted install of the same product - cannot be told apart from inside: the address an echo measures one
/// of them by is the address it measures the other by, and only one of the two adapters is ever reached.
/// </summary>
public static class TunnelOverlap
{
    /// <summary>
    /// Whether both interfaces would be measured through one address.
    /// </summary>
    public static bool Same(IEnumerable<string> addresses, IEnumerable<string> other)
    {
        var mine = LinkLossProbe.PeerTargets(addresses);
        return LinkLossProbe.PeerTargets(other).Any(target => mine.Contains(target, StringComparer.Ordinal));
    }
}
