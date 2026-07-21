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
    IReadOnlyList<string> Apps);
