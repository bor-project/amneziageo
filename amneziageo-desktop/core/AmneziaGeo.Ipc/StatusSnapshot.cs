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
    bool RouteLog = false,
    // Structured connect-failure reason (ConnectFailureReason name); empty unless the last connect failed.
    string ConnectFailReason = "",
    // Short cause label for the failed connect (e.g. sc error name); never secrets.
    string ConnectFailDetail = "",
    // Transient-failure retry count for the current dial; 0 when not retrying.
    int RetryAttempt = 0,
    // Auto-connect the selected profile on service start (survive a reboot).
    bool SurviveReboot = false,
    // Retry a desired connection at a fixed interval instead of the default backoff.
    bool PeriodicReconnect = false,
    // Interval between periodic auto-reconnect attempts, in seconds.
    int PeriodicReconnectIntervalSeconds = 30,
    // Show tray notifications for connection state changes.
    bool ShowNotifications = true);
