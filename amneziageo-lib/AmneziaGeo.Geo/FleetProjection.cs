using AmneziaGeo.Decl;
using AmneziaGeo.Routing;

namespace AmneziaGeo.Geo;

/// <summary>
/// What one server is given to carry: the ranges, names and applications of the rules resolved onto it.
/// </summary>
public sealed record ServerProjection(
    string Server,
    IReadOnlyList<string> Routes,
    IReadOnlyList<GeoDomain> Domains,
    IReadOnlyList<string> Apps);

/// <summary>
/// The list materialized against the fleet: one projection per server that is up, and the blocking bucket, which
/// gains what a rule drops while the server it names is down.
/// </summary>
public sealed record FleetProjection(
    IReadOnlyList<ServerProjection> Servers,
    IReadOnlyList<string> BlockRoutes,
    IReadOnlyList<GeoDomain> BlockDomains,
    RoutingPlan Plan);
