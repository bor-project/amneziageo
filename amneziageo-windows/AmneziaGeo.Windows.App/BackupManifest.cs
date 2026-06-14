namespace AmneziaGeo.Windows.App;

/// <summary>
/// Metadata entry stored in a backup archive to identify and validate it.
/// </summary>
internal sealed record BackupManifest(
    string Format,
    int FormatVersion,
    DateTimeOffset CreatedUtc,
    string AppVersion,
    bool HasStateDb,
    IReadOnlyList<string> Configs);
