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

    /// <summary>
    /// Paths to default configs to register on startup.
    /// </summary>
    public string[] ConfigPaths { get; set; } = [];

    /// <summary>
    /// Shared routing lists to ensure exist on startup.
    /// </summary>
    public RoutingListSpec[] RoutingLists { get; set; } = [];

    /// <summary>
    /// Profiles to ensure exist on startup, with optional routing-list assignment.
    /// </summary>
    public ProfileSpec[] Profiles { get; set; } = [];

    /// <summary>
    /// Per-config WebSocket (UDP-over-TCP) transport to seed on startup.
    /// </summary>
    public WebSocketSpec[] WebSockets { get; set; } = [];

    /// <summary>
    /// Run the startup seed only once per install.
    /// </summary>
    public bool SeedOnce { get; set; }
}

/// <summary>
/// Bootstrap spec for a config's WebSocket transport (wstunnel).
/// </summary>
internal sealed class WebSocketSpec
{
    /// <summary>
    /// The config name whose transport to set (must already be registered via ConfigPaths).
    /// </summary>
    public string Config { get; set; } = string.Empty;

    /// <summary>
    /// Whether the WebSocket transport is turned on.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The TLS port of the wstunnel server (usually 443).
    /// </summary>
    public int Port { get; set; } = 443;

    /// <summary>
    /// Server address for the WebSocket transport.
    /// </summary>
    public string Host { get; set; } = string.Empty;
}

/// <summary>
/// Bootstrap spec for a shared routing list.
/// </summary>
internal sealed class RoutingListSpec
{
    /// <summary>
    /// The routing list name (also the key for upsert).
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Rule tokens for the list.
    /// </summary>
    public string[] Rules { get; set; } = [];
}

/// <summary>
/// Bootstrap spec for a profile.
/// </summary>
internal sealed class ProfileSpec
{
    /// <summary>
    /// Profile name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The single config this profile binds.
    /// </summary>
    public string Config { get; set; } = string.Empty;

    /// <summary>
    /// Routing list to attach by name, or null to leave unassigned.
    /// </summary>
    public string? RoutingList { get; set; }

    /// <summary>
    /// Whether the routing list is enabled.
    /// </summary>
    public bool UseRouting { get; set; }
}
