namespace AmneziaGeo.Decl;

/// <summary>
/// The current materialized set and generation of the routing list actively projected onto a running
/// tunnel. The generation is bumped whenever the list's materialized routes/domains change (a source
/// refresh or a rule edit re-materializes it), so the live tunnel can cheaply detect that its geo address
/// set went stale and needs re-applying via UAPI, without diffing the whole set every poll (#83).
/// </summary>
public sealed record ActiveRoutingListMaterialization(
    long ListId,
    long Generation,
    IReadOnlyList<string> Routes,
    IReadOnlyList<GeoDomain> Domains);
