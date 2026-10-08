namespace AmneziaGeo.Decl;

/// <summary>
/// A routing list as the catalogue shows it: its name, how many rules each bucket holds, the totals of the
/// materialized routes and domains, the traffic policy the list carries, the configuration it arrived with (empty for
/// a list made on the device) and whether its server holds a newer version.
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
    bool UseGlobalProxy,
    string Source = "",
    bool HasUpdate = false);
