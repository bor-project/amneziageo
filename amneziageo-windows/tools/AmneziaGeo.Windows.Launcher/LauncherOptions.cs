namespace AmneziaGeo.Windows.Launcher;

/// <summary>
/// Launcher options bound from the "Launcher" section of appsettings.json.
/// </summary>
internal sealed class LauncherOptions
{
    /// <summary>
    /// The default balancer or config name to launch when none is passed on the command line.
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
    /// Paths to default Amnezia configs to register on startup; each entry is a .conf file or a directory scanned for *.conf, and environment variables are expanded.
    /// </summary>
    public string[] ConfigPaths { get; set; } = [];

    /// <summary>
    /// Shared routing lists to ensure exist on startup.
    /// </summary>
    public RoutingListSpec[] RoutingLists { get; set; } = [];

    /// <summary>
    /// Profiles (balancer groups) to ensure exist on startup, with optional routing-list assignment.
    /// </summary>
    public ProfileSpec[] Profiles { get; set; } = [];
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
    /// Rule tokens like "geosite:openai", "geoip:de", "domain:example.com", "cidr:1.2.3.0/24".
    /// </summary>
    public string[] Rules { get; set; } = [];
}

/// <summary>
/// Bootstrap spec for a profile (balancer group).
/// </summary>
internal sealed class ProfileSpec
{
    /// <summary>
    /// The profile name (also the key for upsert; matches the balancer group name).
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Balancer mode: "priority" (default) or "latency".
    /// </summary>
    public string Mode { get; set; } = "priority";

    /// <summary>
    /// Recheck interval in seconds.
    /// </summary>
    public int RecheckSeconds { get; set; } = 60;

    /// <summary>
    /// Member config names, in priority order.
    /// </summary>
    public string[] Members { get; set; } = [];

    /// <summary>
    /// Routing list to attach by name, or null to leave unassigned.
    /// </summary>
    public string? RoutingList { get; set; }

    /// <summary>
    /// Whether the routing list is enabled (use_routing flag).
    /// </summary>
    public bool UseRouting { get; set; }
}
