namespace AmneziaGeo.Ipc;

/// <summary>
/// A geo data source as shown in the UI: the download source plus its last-update metadata.
/// <paramref name="Updated"/> is null when the source has never been downloaded.
/// <paramref name="Updating"/> is true while a download / re-materialize for this source is in flight;
/// <paramref name="Progress"/> is the download percent (0-100) while downloading, or -1 once the file is
/// in hand and the routing lists are being re-materialized (indeterminate, spinner only).
/// </summary>
public sealed record SourceEntry(
    string Name,
    string Kind,
    string Url,
    string? Updated,
    int CategoryCount,
    bool Updating = false,
    int Progress = 0);
