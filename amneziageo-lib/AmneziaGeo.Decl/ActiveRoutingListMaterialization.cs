namespace AmneziaGeo.Decl;

/// <summary>
/// Materialized share of the routing list projected onto a running tunnel, and the generation of the list it
/// was cut from.
/// </summary>
public sealed record ActiveRoutingListMaterialization(
    long ListId,
    long Generation,
    IReadOnlyList<string> Routes,
    IReadOnlyList<GeoDomain> Domains,
    IReadOnlyList<string> DirectRoutes,
    IReadOnlyList<GeoDomain> DirectDomains,
    IReadOnlyList<string> BlockRoutes,
    IReadOnlyList<GeoDomain> BlockDomains);
