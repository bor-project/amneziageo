namespace AmneziaGeo.Windows.App;

/// <summary>
/// Where a tunnel stands on the machine: the list it routes through, whether it carries only what that list names,
/// and whether it takes the default route from whoever holds it.
/// </summary>
internal sealed record TunnelRole(long? ListId, bool Split, bool Preferred);
