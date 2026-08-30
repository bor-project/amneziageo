namespace AmneziaGeo.Decl;

/// <summary>
/// Materialized share of the routing list projected onto a running tunnel, the generation of the list it was
/// cut from and the stamp of the cut itself.
/// </summary>
public sealed record ActiveRoutingListMaterialization(
    long ListId,
    long Generation,
    long Share,
    IReadOnlyList<string> Routes,
    IReadOnlyList<GeoDomain> Domains,
    IReadOnlyList<string> DirectRoutes,
    IReadOnlyList<GeoDomain> DirectDomains,
    IReadOnlyList<string> BlockRoutes,
    IReadOnlyList<GeoDomain> BlockDomains);
