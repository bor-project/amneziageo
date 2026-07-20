namespace AmneziaGeo.Ipc;

/// <summary>
/// A geo data source as shown in the UI: the download source plus its last-update metadata. Updated is
/// null when the source has never been downloaded. Updating is true while a download is in flight.
/// Progress is the download percent (0-100), or -1 while re-materializing. UpdateAvailable is true when
/// the last update-check found a newer remote file. Error is the last download/parse failure, or null.
/// </summary>
public sealed record SourceEntry(
    string Name,
    string Kind,
    string Url,
    string? Updated,
    int CategoryCount,
    bool Updating = false,
    int Progress = 0,
    bool UpdateAvailable = false,
    string? Error = null);
