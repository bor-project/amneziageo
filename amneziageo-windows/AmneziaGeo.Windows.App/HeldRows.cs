using AmneziaGeo.Routing;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Builds the rows of the session report from what the cache, the tracker and the standing ranges hold.
/// </summary>
internal static class HeldRows
{
    /// <summary>
    /// The row of a destination the cache holds. A route the tracker installed for a matched app or for all-UDP
    /// names the path and the reason where no rule and no tracked name settled the address.
    /// </summary>
    public static AmneziaGeo.Ipc.LiveSession Of(RoutingCache.Held held, bool split, string name, bool named, bool? datagrams)
    {
        var untold = held.Adopted ? !named : !held.ByName && !held.ByApp && held.Verdict == RouteVerdict.None;
        var routed = datagrams is not null && untold;
        return new AmneziaGeo.Ipc.LiveSession(
            held.Address.ToString(),
            held.Verdict == RouteVerdict.None ? AmneziaGeo.Ipc.LiveSession.Undecided : held.Verdict.ToString().ToLowerInvariant(),
            IdleSeconds: held.IdleSeconds,
            Name: name,
            Path: routed ? AmneziaGeo.Ipc.LiveSession.PathTunnel : PathOf(held, split),
            Reason: routed ? Routed(datagrams == true) : ReasonOf(held),
            LeftSeconds: Math.Max(held.TtlSeconds - held.IdleSeconds, 0));
    }

    /// <summary>
    /// The row of an address only the tracker routes, for a matched app or for all-UDP.
    /// </summary>
    public static AmneziaGeo.Ipc.LiveSession OfRouted(string address, bool datagrams, int idleSeconds, string name)
    {
        return new AmneziaGeo.Ipc.LiveSession(address, AmneziaGeo.Ipc.LiveSession.Undecided, IdleSeconds: idleSeconds,
            Name: name, Path: AmneziaGeo.Ipc.LiveSession.PathTunnel, Reason: Routed(datagrams));
    }

    /// <summary>
    /// The row of a range held outside the cache for the connection itself; one kept past the tunnel reads direct.
    /// </summary>
    public static AmneziaGeo.Ipc.LiveSession OfStanding(string range, bool past)
    {
        return new AmneziaGeo.Ipc.LiveSession(range, past ? "direct" : "proxy",
            Path: past ? AmneziaGeo.Ipc.LiveSession.PathDirect : AmneziaGeo.Ipc.LiveSession.PathTunnel,
            Reason: AmneziaGeo.Ipc.LiveSession.ReasonService);
    }

    // What brought an address the tracker routes.
    private static string Routed(bool datagrams)
    {
        return datagrams ? AmneziaGeo.Ipc.LiveSession.ReasonUdp : AmneziaGeo.Ipc.LiveSession.ReasonApp;
    }

    // What settled the destination, in the order the cache settles it.
    private static string ReasonOf(RoutingCache.Held held)
    {
        if (held.Adopted)
        {
            return AmneziaGeo.Ipc.LiveSession.ReasonResolved;
        }

        if (held.ByName)
        {
            return AmneziaGeo.Ipc.LiveSession.ReasonName;
        }

        if (held.Verdict != RouteVerdict.None)
        {
            return AmneziaGeo.Ipc.LiveSession.ReasonRange;
        }

        return held.ByApp ? AmneziaGeo.Ipc.LiveSession.ReasonApp : AmneziaGeo.Ipc.LiveSession.ReasonNone;
    }

    // Where the address actually goes. An entry the verdict installed nothing for follows the default of the mode.
    private static string PathOf(RoutingCache.Held held, bool split)
    {
        return held.Plan switch
        {
            RoutePlan.Tunnel or RoutePlan.External => AmneziaGeo.Ipc.LiveSession.PathTunnel,
            RoutePlan.Permit or RoutePlan.Bypass => AmneziaGeo.Ipc.LiveSession.PathDirect,
            RoutePlan.Drop => AmneziaGeo.Ipc.LiveSession.PathBlock,
            _ => split ? AmneziaGeo.Ipc.LiveSession.PathDirect : AmneziaGeo.Ipc.LiveSession.PathTunnel,
        };
    }
}
