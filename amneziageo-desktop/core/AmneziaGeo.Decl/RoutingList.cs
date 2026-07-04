namespace AmneziaGeo.Decl;

/// <summary>
/// A named shared routing list with its materialized active set. Profiles reference it by id.
/// </summary>
public sealed record RoutingList(
    long Id,
    string Name,
    IReadOnlyList<GeoRule> Rules,
    IReadOnlyList<string> Routes,
    IReadOnlyList<GeoDomain> Domains,
    IReadOnlyList<string> Apps);
