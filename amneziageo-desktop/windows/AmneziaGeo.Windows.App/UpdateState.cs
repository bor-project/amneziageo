namespace AmneziaGeo.Windows.App;

/// <summary>
/// Holds the most recent update-check result, shared between the periodic <see cref="UpdateCheckService"/>,
/// the IPC check handler, and the snapshot builder. A single reference assignment is atomic, which is all
/// the cross-thread visibility this needs.
/// </summary>
internal sealed class UpdateState
{
    public UpdateInfo? Latest { get; set; }
}
