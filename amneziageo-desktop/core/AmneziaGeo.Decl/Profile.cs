namespace AmneziaGeo.Decl;

/// <summary>
/// A named tunnel profile bound to one configuration.
/// </summary>
public sealed record Profile(
    string Name,
    string Config);
