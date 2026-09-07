namespace AmneziaGeo.Decl;

/// <summary>
/// A routing list without its materialized buckets: the generation those buckets stand at, and the rules
/// they were built from.
/// </summary>
public sealed record RoutingListStamp(long Id, string Name, long Generation, IReadOnlyList<GeoRule> Rules);
