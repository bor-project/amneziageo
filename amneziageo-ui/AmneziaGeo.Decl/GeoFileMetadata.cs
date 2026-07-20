namespace AmneziaGeo.Decl;

/// <summary>
/// Metadata of a downloaded geo database file. ETag and LastModified enable conditional update checks.
/// </summary>
public sealed record GeoFileMetadata(
    string Name,
    string SourceUrl,
    DateTimeOffset UpdatedAt,
    string Sha256,
    int CategoryCount,
    string ETag = "",
    string LastModified = "");
