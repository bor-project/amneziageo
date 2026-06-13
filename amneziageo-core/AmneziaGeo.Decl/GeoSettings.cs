namespace AmneziaGeo.Decl;

/// <summary>
/// Per-tunnel geo split-tunneling settings.
/// </summary>
public sealed record GeoSettings(bool GeoSplit, IReadOnlyList<GeoRule> Rules);
