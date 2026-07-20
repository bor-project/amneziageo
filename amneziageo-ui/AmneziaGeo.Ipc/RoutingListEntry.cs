namespace AmneziaGeo.Ipc;

/// <summary>
/// A shared routing list, summarized for the UI: id, name, and counts of its rules and
/// materialized routes / domains. Full rule tokens are fetched on demand via the get-routing-list
/// command to keep the snapshot small.
/// </summary>
public sealed record RoutingListEntry(
    long Id,
    string Name,
    int RuleCount,
    int RouteCount,
    int DomainCount);
