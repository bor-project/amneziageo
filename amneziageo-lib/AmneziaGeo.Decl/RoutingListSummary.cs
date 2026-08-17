namespace AmneziaGeo.Decl;

/// <summary>
/// A routing list as the catalogue shows it: its name, how many rules each bucket holds, the totals of the
/// materialized routes and domains, and the traffic policy the list carries.
/// </summary>
public sealed record RoutingListSummary(
    long Id,
    string Name,
    int RuleCount,
    int RouteCount,
    int DomainCount,
    int ProxyRuleCount,
    int DirectRuleCount,
    int BlockRuleCount,
    bool AllUdp,
    bool UseGlobalProxy);
