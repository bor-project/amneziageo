namespace AmneziaGeo.Ipc;

/// <summary>
/// A tunnel profile: its single configuration, current runtime status, and routing assignment.
/// </summary>
public sealed record BalancerEntry(
    string Name,
    string Status,
    string Config,
    long? RoutingListId = null,
    bool UseRouting = false);
