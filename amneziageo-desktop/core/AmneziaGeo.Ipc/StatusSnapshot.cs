namespace AmneziaGeo.Ipc;

/// <summary>
/// A full picture of the agent's configurations, profiles, routing lists,
/// and their statuses.
/// </summary>
public sealed record StatusSnapshot(
    string AgentVersion,
    string? BoundTarget,
    IReadOnlyList<ConfigEntry> Configs,
    IReadOnlyList<ProfileEntry> Profiles,
    IReadOnlyList<RoutingListEntry>? RoutingLists = null,
    bool Active = true,
    string BoundStatus = ConnectionStatus.Disconnected,
    bool RestartRequired = false,
    string? SelectedTarget = null,
    IReadOnlyList<SourceEntry>? Sources = null,
    IReadOnlyList<string>? Logs = null,
    string UpdateUrl = "",
    bool UpdateAvailable = false,
    string UpdateVersion = "",
    string UpdateSetupUrl = "",
    string UpdateDescription = "",
    bool GeoAutoCheck = true,
    int GeoCheckIntervalHours = 24,
    // How long the materialized geo address cache stays current before a background refresh.
    int GeoCacheValidityHours = 24,
    bool ConnectFailed = false,
    // AmneziaWG engine version (git describe of the bundled submodule). Authoritative engine version.
    // Empty when the build could not resolve it.
    string EngineVersion = "",
    // When set, all UDP is routed through the tunnel while in split mode.
    bool TunnelAllUdp = false,
    // Current log verbosity token: "error" (default), "info", "debug", or "trace".
    string LogLevel = "error",
    // Whether the dedicated routing log (routes.log) is currently recording.
    bool RouteLog = false);
