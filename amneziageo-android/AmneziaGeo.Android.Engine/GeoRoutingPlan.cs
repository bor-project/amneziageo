using AmneziaGeo.Decl;
using AmneziaGeo.Routing;

namespace AmneziaGeo.Android.Engine;

/// <summary>
/// The rules one session routes by. Addresses go to the router as ranges and names stay as rules the resolved
/// answers are matched against - nothing is turned into system routes, so a name that moves keeps its verdict.
/// </summary>
public sealed record GeoRoutingPlan(
    IReadOnlyList<string> ProxyRoutes,
    IReadOnlyList<string> DirectRoutes,
    IReadOnlyList<string> BlockRoutes,
    IReadOnlyList<GeoDomain> ProxyDomains,
    IReadOnlyList<GeoDomain> DirectDomains,
    IReadOnlyList<GeoDomain> BlockDomains,
    bool FullTunnel,
    bool AllUdp,
    int TtlSeconds = 300)
{
    /// <summary>
    /// Applications the list names: what no other rule decides for them rides the tunnel.
    /// </summary>
    public IReadOnlyList<string> TunnelApps { get; init; } = [];

    /// <summary>
    /// Whether every connection gets its own verdict; off leaves every destination to the route table.
    /// </summary>
    public bool UseRouter { get; init; } = true;

    /// <summary>
    /// Entries a list holds in the route table before the relay decides.
    /// </summary>
    public int SplitLimit { get; init; } = SplitBudget.DefaultLimit;

    /// <summary>
    /// Whether a part of the list is decided on the fly: the rules heavier than half the threshold and the ones past it.
    /// </summary>
    public bool Live { get; init; }

    /// <summary>
    /// The ranges the route table holds where a part of the list is decided on the fly.
    /// </summary>
    public IReadOnlyList<string> HeldRoutes { get; init; } = [];

    /// <summary>
    /// The names asked ahead and held in the route table where a part of the list is decided on the fly.
    /// </summary>
    public IReadOnlyList<GeoDomain> HeldDomains { get; init; } = [];

    /// <summary>
    /// What the held part of the list weighs against the threshold.
    /// </summary>
    public int HeldWeight { get; init; }

    /// <summary>
    /// What the part decided on the fly weighs.
    /// </summary>
    public int LiveWeight { get; init; }

    /// <summary>
    /// The ranges of these the route table holds: all of them while nothing is decided on the fly.
    /// </summary>
    public IReadOnlyList<string> Held(IReadOnlyList<string> ranges)
    {
        if (!Live)
        {
            return ranges;
        }

        var held = new HashSet<string>(HeldRoutes, StringComparer.OrdinalIgnoreCase);
        return [.. ranges.Where(held.Contains)];
    }

    /// <summary>
    /// The names of these asked ahead and held in the route table: all of them while nothing is decided on the fly.
    /// </summary>
    public IReadOnlyList<GeoDomain> Held(IReadOnlyList<GeoDomain> names)
    {
        if (!Live)
        {
            return names;
        }

        var held = new HashSet<GeoDomain>(HeldDomains);
        return [.. names.Where(held.Contains)];
    }

    /// <summary>
    /// Everything through the tunnel, nothing listed.
    /// </summary>
    public static GeoRoutingPlan Full { get; } = new([], [], [], [], [], [], true, false);

    /// <summary>
    /// Whether any name has to be matched against resolved answers.
    /// </summary>
    public bool HasDomains => ProxyDomains.Count > 0 || DirectDomains.Count > 0 || BlockDomains.Count > 0;

    /// <summary>
    /// Whether any rule can change a destination's route.
    /// </summary>
    public bool HasRules => ProxyRoutes.Count > 0 || DirectRoutes.Count > 0 || BlockRoutes.Count > 0 || HasDomains;
}
