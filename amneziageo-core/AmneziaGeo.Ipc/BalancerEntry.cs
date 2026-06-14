namespace AmneziaGeo.Ipc;

/// <summary>
/// A balancer group, its mode, and its current runtime status.
/// </summary>
public sealed record BalancerEntry(
    string Name,
    string Mode,
    string Status,
    string? ActiveMember,
    IReadOnlyList<string> Members);
