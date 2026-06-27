namespace AmneziaGeo.Decl;

/// <summary>
/// A named tunnel profile bound to exactly one configuration. An empty <see cref="Config"/>
/// means the profile has no configuration yet (freshly created, pending import).
/// </summary>
public sealed record BalancerGroup(
    string Name,
    string Config);
