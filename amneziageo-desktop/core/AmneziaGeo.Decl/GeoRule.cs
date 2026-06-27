namespace AmneziaGeo.Decl;

/// <summary>
/// A single split-tunnel geo rule.
/// </summary>
public sealed record GeoRule(GeoRuleKind Kind, string Value);
