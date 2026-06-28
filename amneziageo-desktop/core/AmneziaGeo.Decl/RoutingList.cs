namespace AmneziaGeo.Decl;

/// <summary>
/// A named, shared routing list (geo + manual rules) with its materialized active set.
/// Profiles reference a routing list by id (many-to-one) and toggle its use with
/// a per-profile flag.
/// </summary>
public sealed record RoutingList(
    long Id,
    string Name,
    IReadOnlyList<GeoRule> Rules,
    IReadOnlyList<string> Routes,
    IReadOnlyList<GeoDomain> Domains,
    IReadOnlyList<string> Apps);
