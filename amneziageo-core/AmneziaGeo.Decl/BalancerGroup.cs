namespace AmneziaGeo.Decl;

/// <summary>
/// An ordered failover group of tunnel configurations with a recheck interval.
/// </summary>
public sealed record BalancerGroup(
    string Name,
    int RecheckSeconds,
    IReadOnlyList<string> Members);
