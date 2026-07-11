namespace AmneziaGeo.Decl;

/// <summary>
/// A profile's live runtime state: the connection status. UpdatedAt is stamped by the store on each save.
/// </summary>
public sealed record ProfileState(
    string Name,
    string Status,
    DateTimeOffset UpdatedAt);
