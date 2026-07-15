namespace AmneziaGeo.Windows.Launcher;

/// <summary>
/// Launcher options bound from the "Launcher" section of appsettings.json.
/// </summary>
internal sealed class LauncherOptions
{
    /// <summary>
    /// The default profile or config name to launch when none is passed on the command line.
    /// </summary>
    public string? Target { get; set; }

    /// <summary>
    /// Whether to start the backend agent by default.
    /// </summary>
    public bool RunService { get; set; } = true;

    /// <summary>
    /// Whether to start the desktop UI by default.
    /// </summary>
    public bool RunUi { get; set; } = true;
}
