namespace AmneziaGeo.Decl;

/// <summary>
/// A geo database download source (geosite or geoip).
/// </summary>
public sealed record GeoSource(string Name, string Kind, string Url, int Position);
