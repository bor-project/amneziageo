namespace AmneziaGeo.Ipc;

/// <summary>
/// A balancer group, its mode, recheck interval, current runtime status, and routing assignment.
/// </summary>
public sealed record BalancerEntry(
    string Name,
    string Mode,
    string Status,
    string? ActiveMember,
    IReadOnlyList<string> Members,
    int RecheckSeconds = 0,
    long? RoutingListId = null,
    bool UseRouting = false);
