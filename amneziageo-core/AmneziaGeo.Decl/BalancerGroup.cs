namespace AmneziaGeo.Decl;

/// <summary>
/// An ordered failover group of tunnel configurations with a recheck interval and selection mode.
/// </summary>
public sealed record BalancerGroup(
    string Name,
    int RecheckSeconds,
    IReadOnlyList<string> Members,
    string Mode = "priority");
