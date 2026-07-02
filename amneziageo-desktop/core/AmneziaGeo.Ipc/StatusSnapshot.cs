namespace AmneziaGeo.Ipc;

/// <summary>
/// A full picture of the agent's configurations, balancers (profiles), routing lists,
/// and their statuses.
/// </summary>
public sealed record StatusSnapshot(
    string AgentVersion,
    string? BoundTarget,
    IReadOnlyList<ConfigEntry> Configs,
    IReadOnlyList<BalancerEntry> Balancers,
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
    // How long the materialized geo address cache stays current before a background refresh re-validates
    // the in-use lists (#83). Surfaced next to the sources so the user can tune it.
    int GeoCacheValidityHours = 24,
    bool ConnectFailed = false,
    // AmneziaWG engine version (git describe of the bundled amneziawg-windows submodule, baked at
    // build time). tunnel.dll carries no version resource, so this is the authoritative engine
    // version. Empty when the build could not resolve it.
    string EngineVersion = "",
    // When set, all UDP is routed through the tunnel while in split mode (#77-udp).
    bool TunnelAllUdp = false,
    // Current log verbosity token: "info" (default), "debug", or "trace". Surfaced so the UI can show and
    // change the level; a support engineer raises it to "trace" to capture every connect step (#82).
    string LogLevel = "info");
