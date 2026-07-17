namespace AmneziaGeo.Windows.App;

/// <summary>
/// Holds the latest update-check result and the download phase reported by the UI process that owns the
/// setup byte-pump, so the tray and every window share one update state.
/// </summary>
internal sealed class UpdateState
{
    public UpdateInfo? Latest { get; set; }

    /// <summary>
    /// The current setup download phase.
    /// </summary>
    public UpdateDownloadPhase DownloadPhase { get; set; }

    /// <summary>
    /// Download progress in percent (0..100).
    /// </summary>
    public int DownloadPercent { get; set; }

    /// <summary>
    /// Full path of the downloaded setup, set when the phase is Downloaded.
    /// </summary>
    public string DownloadedSetupPath { get; set; } = string.Empty;

    /// <summary>
    /// The version the downloaded setup carries, matched against Latest to drop a stale download.
    /// </summary>
    public string DownloadedVersion { get; set; } = string.Empty;

    /// <summary>
    /// Whether the last download failed; rides the snapshot so the tray warns, cleared when a download starts.
    /// </summary>
    public bool DownloadFailed { get; set; }

    /// <summary>
    /// Whether a running download has been asked to cancel; relayed to the UI that owns the byte-pump.
    /// </summary>
    public bool CancelRequested { get; set; }
}

/// <summary>
/// The setup download phase.
/// </summary>
internal enum UpdateDownloadPhase
{
    Idle,
    Downloading,
    Downloaded,
}
