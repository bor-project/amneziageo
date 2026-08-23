namespace AmneziaGeo.Ipc;

/// <summary>
/// Why this machine cannot raise an access point. Each token carries its own remedy, so the window words them apart.
/// </summary>
public static class HotspotReasons
{
    /// <summary>
    /// Nothing stands in the way.
    /// </summary>
    public const string Ready = "";

    /// <summary>
    /// No wireless adapter on this machine.
    /// </summary>
    public const string NoAdapter = "no-adapter";

    /// <summary>
    /// Wireless adapter present with its radio off.
    /// </summary>
    public const string RadioOff = "radio-off";

    /// <summary>
    /// Wireless adapter that does not run as an access point.
    /// </summary>
    public const string NoApMode = "no-ap-mode";

    /// <summary>
    /// Programs the access point is built out of are not installed.
    /// </summary>
    public const string NoTools = "no-tools";

    /// <summary>
    /// System service the access point rides on is stopped.
    /// </summary>
    public const string ServiceOff = "service-off";

    /// <summary>
    /// Platform without an access point of its own.
    /// </summary>
    public const string NoPlatform = "no-platform";
}
