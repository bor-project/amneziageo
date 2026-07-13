namespace AmneziaGeo.Decl;

/// <summary>
/// A single split-tunnel geo rule with the list bucket it belongs to.
/// </summary>
public sealed record GeoRule(GeoRuleKind Kind, string Value, RouteRole Role = RouteRole.Proxy);
