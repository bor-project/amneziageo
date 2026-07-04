namespace AmneziaGeo.Decl;

/// <summary>
/// Materialized set and generation of the routing list projected onto a running tunnel.
/// </summary>
public sealed record ActiveRoutingListMaterialization(
    long ListId,
    long Generation,
    IReadOnlyList<string> Routes,
    IReadOnlyList<GeoDomain> Domains);
