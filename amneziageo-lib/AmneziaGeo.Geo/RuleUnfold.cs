using AmneziaGeo.Decl;
using AmneziaGeo.Routing;

namespace AmneziaGeo.Geo;

/// <summary>
/// One rule of a list unfolded apart: the ranges and the names it brings to its bucket.
/// </summary>
public sealed record RuleUnfold(GeoRule Rule, IReadOnlyList<string> Routes, IReadOnlyList<GeoDomain> Domains)
{
    /// <summary>
    /// What the rule weighs against the threshold of the route table.
    /// </summary>
    public int Weight => SplitBudget.Weight(Routes.Count, Domains.Count);
}
