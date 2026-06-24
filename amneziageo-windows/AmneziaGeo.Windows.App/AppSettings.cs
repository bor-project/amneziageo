namespace AmneziaGeo.Windows.App;

/// <summary>
/// Runtime tuning settings persisted in the state database.
/// </summary>
internal sealed record AppSettings
{
    /// <summary>
    /// How often tunneled domains are re-resolved, in seconds.
    /// </summary>
    public int RefreshSeconds { get; init; } = 60;

    /// <summary>
    /// How long a connect attempt waits for a handshake before declaring the server unreachable, in seconds.
    /// </summary>
    public int ConnectTimeoutSeconds { get; init; } = 20;

    /// <summary>
    /// Handshake age beyond which a connected tunnel is treated as dead (triggering a re-dial), in seconds.
    /// </summary>
    public int DeadThresholdSeconds { get; init; } = 180;

    /// <summary>
    /// URL of the update metadata file (JSON with version/description/setup). Empty disables update
    /// checks. The installer is expected to sit next to this file (resolved relative to it).
    /// </summary>
    public string UpdateUrl { get; init; } = string.Empty;

    /// <summary>
    /// When set, the agent periodically checks the geo sources for a newer remote file (without
    /// downloading) and the UI badges/notifies. Defaults on.
    /// </summary>
    public bool GeoAutoCheck { get; init; } = true;

    /// <summary>
    /// How often the periodic geo-source update-check runs, in hours.
    /// </summary>
    public int GeoCheckIntervalHours { get; init; } = 24;
}
