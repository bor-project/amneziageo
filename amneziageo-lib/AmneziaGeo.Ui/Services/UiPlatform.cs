namespace AmneziaGeo.Ui.Services;

/// <summary>
/// Platform traits the shared UI reacts to. The host sets them once at startup.
/// </summary>
internal static class UiPlatform
{
    /// <summary>
    /// Whether the app runs on a television, where a remote drives focus and every focused control needs a ring.
    /// </summary>
    public static bool IsTelevision { get; set; }

    /// <summary>
    /// Whether a geo rule can be unfolded into the entries it covers. Off on Android, where a country's tens of
    /// thousands of rows cost more memory than the device has to spare.
    /// </summary>
    public static bool SupportsGeoPreview { get; set; } = true;

    /// <summary>
    /// Whether the platform carries the tunnel over a WebSocket proxy. Off on Android, whose engine has no
    /// wstunnel, so the editor states that instead of offering a switch that cannot hold.
    /// </summary>
    public static bool SupportsWebSocket { get; set; } = true;
}
