namespace AmneziaGeo.Decl;

/// <summary>
/// Materialized set of the routing list projected onto a running tunnel: this tunnel's share of it. Generation is
/// the list's own, Share moves whenever the distributor hands this tunnel a different set.
/// </summary>
public sealed record ActiveRoutingListMaterialization(
    long ListId,
    long Generation,
    long Share,
    IReadOnlyList<string> Routes,
    IReadOnlyList<GeoDomain> Domains,
    IReadOnlyList<string> BlockRoutes,
    IReadOnlyList<GeoDomain> BlockDomains);
