namespace AmneziaGeo.Decl;

/// <summary>
/// Metadata about a downloaded geo database file.
/// </summary>
public sealed record GeoFileMetadata(string Name, string SourceUrl, DateTimeOffset UpdatedAt, string Sha256, int CategoryCount);
