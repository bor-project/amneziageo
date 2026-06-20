namespace AmneziaGeo.Decl;

/// <summary>
/// Metadata about a downloaded geo database file. <paramref name="ETag"/> and
/// <paramref name="LastModified"/> are the HTTP validators captured at download time (empty when the
/// server sent none); the update-check uses them for a conditional request so it can tell whether the
/// remote file changed without downloading it again.
/// </summary>
public sealed record GeoFileMetadata(
    string Name,
    string SourceUrl,
    DateTimeOffset UpdatedAt,
    string Sha256,
    int CategoryCount,
    string ETag = "",
    string LastModified = "");
