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
    /// Full path of the downloaded setup.
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

    /// <summary>
    /// Whether a manual update check is running; rides the snapshot so the tray shows a checking state.
    /// </summary>
    public bool Checking { get; set; }

    /// <summary>
    /// Whether the last manual update check failed; rides the snapshot so the tray suppresses the up-to-date notice.
    /// </summary>
    public bool CheckFailed { get; set; }

    /// <summary>
    /// Process id of the setup the window started, set while the phase is Installing.
    /// </summary>
    public int InstallerPid { get; set; }

    /// <summary>
    /// Whether the setup downloaded for this version is on disk, installing or not.
    /// </summary>
    public bool ReadyFor(string? version)
    {
        return DownloadPhase is UpdateDownloadPhase.Downloaded or UpdateDownloadPhase.Installing
            && string.Equals(DownloadedVersion, version, StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns an install to Downloaded once the setup it waits on has ended; answers whether it did.
    /// </summary>
    public bool EndInstall(int pid)
    {
        if (DownloadPhase != UpdateDownloadPhase.Installing || InstallerPid != pid)
        {
            return false;
        }

        DownloadPhase = UpdateDownloadPhase.Downloaded;
        InstallerPid = 0;
        return true;
    }
}

/// <summary>
/// The setup download phase.
/// </summary>
internal enum UpdateDownloadPhase
{
    Idle,
    Downloading,
    Downloaded,
    Installing,
}
