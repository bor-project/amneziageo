using AmneziaGeo.Decl;

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
    bool AllUdp)
{
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
