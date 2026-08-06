namespace AmneziaGeo.Decl;

/// <summary>
/// A config's live runtime state: the connection status. UpdatedAt is stamped by the store on each save.
/// </summary>
public sealed record TunnelState(
    string Name,
    string Status,
    DateTimeOffset UpdatedAt);
