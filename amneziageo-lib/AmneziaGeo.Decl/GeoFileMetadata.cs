namespace AmneziaGeo.Decl;

/// <summary>
/// Metadata of a downloaded geo database file. ETag and LastModified enable conditional update checks,
/// UpdateAvailable holds what the last check found.
/// </summary>
public sealed record GeoFileMetadata(
    string Name,
    string SourceUrl,
    DateTimeOffset UpdatedAt,
    string Sha256,
    int CategoryCount,
    string ETag = "",
    string LastModified = "",
    bool UpdateAvailable = false);
