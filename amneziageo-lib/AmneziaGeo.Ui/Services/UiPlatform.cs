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
}
