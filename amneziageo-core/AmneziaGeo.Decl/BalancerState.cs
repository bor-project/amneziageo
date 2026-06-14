namespace AmneziaGeo.Decl;

/// <summary>
/// A balancer group's live runtime state: the connection status and active member.
/// UpdatedAt is stamped by the store on each save.
/// </summary>
public sealed record BalancerState(
    string Group,
    string Status,
    string? ActiveMember,
    DateTimeOffset UpdatedAt);
