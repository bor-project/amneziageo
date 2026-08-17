namespace AmneziaGeo.Ipc;

/// <summary>
/// A shared routing list, summarized for the UI: id, name, counts of its rules per bucket and of the
/// materialized routes / domains, and the traffic policy it carries. Full rule tokens are fetched on demand
/// via the get-routing-list command to keep the snapshot small.
/// </summary>
public sealed record RoutingListEntry(
    long Id,
    string Name,
    int RuleCount,
    int RouteCount,
    int DomainCount,
    int ProxyRuleCount = 0,
    int DirectRuleCount = 0,
    int BlockRuleCount = 0,
    bool AllUdp = false,
    bool UseGlobalProxy = false);
