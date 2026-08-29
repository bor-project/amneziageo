namespace AmneziaGeo.Decl;

/// <summary>
/// Per-tunnel geo settings together with their materialized active set.
/// </summary>
public sealed record TunnelGeo(
    string Name,
    bool GeoSplit,
    IReadOnlyList<GeoRule> Rules,
    IReadOnlyList<string> Routes,
    IReadOnlyList<GeoDomain> Domains,
    IReadOnlyList<string> Apps,
    IReadOnlyList<string>? DirectRoutes = null,
    IReadOnlyList<GeoDomain>? DirectDomains = null,
    IReadOnlyList<string>? BlockRoutes = null,
    IReadOnlyList<GeoDomain>? BlockDomains = null);
