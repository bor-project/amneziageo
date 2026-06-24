namespace AmneziaGeo.Decl;

/// <summary>
/// A config's preferred DNS server(s) for NON-tunneled (local) name resolution, comma/space separated.
/// Empty means auto-detect the system's current resolvers. Tunneled (geo-matched) names still use the
/// config's own clean resolver, so this never weakens geo resolution. Stored per config (a profile binds
/// one config), so each profile carries its own DNS — moved here from the former global app setting.
/// </summary>
public sealed record ConfigDns(
    string Name,
    string Servers);
