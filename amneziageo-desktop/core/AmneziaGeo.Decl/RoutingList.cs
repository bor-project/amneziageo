namespace AmneziaGeo.Decl;

/// <summary>
/// A named shared routing list with its materialized active set, partitioned by role. Profiles reference it by
/// id. Routes/Domains/Apps hold the proxy bucket (tunneled in split); Direct* the bypass bucket (off-tunnel in
/// full); Block* the always-blocked bucket; Exclude* the always-off-tunnel bucket (bypass in either mode).
/// </summary>
public sealed record RoutingList(
    long Id,
    string Name,
    IReadOnlyList<GeoRule> Rules,
    IReadOnlyList<string> Routes,
    IReadOnlyList<GeoDomain> Domains,
    IReadOnlyList<string> Apps,
    IReadOnlyList<string> DirectRoutes,
    IReadOnlyList<GeoDomain> DirectDomains,
    IReadOnlyList<string> BlockRoutes,
    IReadOnlyList<GeoDomain> BlockDomains,
    IReadOnlyList<string> ExcludeRoutes,
    IReadOnlyList<GeoDomain> ExcludeDomains);
