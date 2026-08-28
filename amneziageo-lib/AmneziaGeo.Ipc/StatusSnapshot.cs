using AmneziaGeo.Ipc.Fleet;

namespace AmneziaGeo.Ipc;

/// <summary>
/// A full picture of the agent's configurations, routing lists, and their statuses.
/// </summary>
public sealed record StatusSnapshot(
    string AgentVersion,
    string? BoundTarget,
    IReadOnlyList<ConfigEntry> Configs,
    IReadOnlyList<RoutingListEntry>? RoutingLists = null,
    bool Active = true,
    string BoundStatus = ConnectionStatus.Disconnected,
    bool RestartRequired = false,
    string? SelectedTarget = null,
    // Routing list every config uses; null routes everything through the tunnel.
    long? SelectedRoutingList = null,
    IReadOnlyList<SourceEntry>? Sources = null,
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
    // Current log verbosity token: "error" (default), "warning", "info", "debug", or "trace".
    string LogLevel = "error",
    // Whether the dedicated routing log (routes.log) is currently recording.
    bool RouteLog = false,
    // Structured connect-failure reason (ConnectFailureReason name); empty unless the last connect failed.
    string ConnectFailReason = "",
    // Short cause label for the failed connect (e.g. sc error name); never secrets.
    string ConnectFailDetail = "",
    // Transient-failure retry count for the current dial; 0 when not retrying.
    int RetryAttempt = 0,
    // Auto-connect the selected config on service start (survive a reboot).
    bool SurviveReboot = false,
    // Retry a desired connection at a fixed interval instead of the default backoff.
    bool PeriodicReconnect = false,
    // Interval between periodic auto-reconnect attempts, in seconds.
    int PeriodicReconnectIntervalSeconds = 30,
    // Idle window a routed destination survives before its route and filters are reclaimed, in seconds.
    int RouteTtlSeconds = 300,
    // Show tray notifications for connection state changes.
    bool ShowNotifications = true,
    // Whether the update check offers prereleases (user-toggleable, defaults to the baked channel).
    bool AllowPrerelease = false,
    // Published SHA-256 of the setup for the current build target; empty on a legacy manifest.
    string UpdateSetupSha256 = "",
    // Whether a setup download is in progress.
    bool UpdateDownloading = false,
    // Whether the setup for the available version is downloaded and ready to install.
    bool UpdateDownloaded = false,
    // Setup download progress in percent (0..100).
    int UpdateDownloadPercent = 0,
    // Full path of the downloaded setup, set when UpdateDownloaded is true.
    string UpdateSetupPath = "",
    // Whether the last disconnect failed to complete (the service refused to stop); the connected state is kept.
    bool DisconnectFailed = false,
    // Short cause label for the failed disconnect (service state); never secrets.
    string DisconnectFailDetail = "",
    // Whether the last setup download failed; its rising edge fires a tray warning balloon, cleared on the next start.
    bool UpdateDownloadFailed = false,
    // Whether a running setup download has been asked to cancel; the UI that owns the byte-pump aborts it.
    bool UpdateCancelRequested = false,
    // Whether a manual update check (tray/console "Check for updates") is currently running.
    bool UpdateChecking = false,
    // Whether the last manual update check failed to complete (server unreachable or unreadable metadata).
    bool UpdateCheckFailed = false,
    // Monotonic counter bumped when a geo-source refresh actually changed the local bases; a rise drives the tray balloon.
    int GeoUpdatedTick = 0,
    // Build target (win-<arch> for self-contained, win-<arch>-fdd for framework-dependent) baked at build time; drives the About build-type row.
    string BuildTarget = "",
    // Whether the package manager is installing the downloaded update; only the agent-owned flow (Linux) sets it.
    bool UpdateInstalling = false,
    // Whether the system runs this application as its always-on VPN. Only a running Android tunnel can be asked,
    // so it stays false while the tunnel is down.
    bool AlwaysOn = false,
    // Whether always-on also blocks what would leave outside the tunnel.
    bool AlwaysOnLockdown = false,
    // Whether the local proxy listens on its ports.
    bool ProxyEnabled = false,
    // SOCKS5 port of the local proxy; the v2ray family looks for it here.
    int ProxySocksPort = 10808,
    // HTTP port of the local proxy.
    int ProxyHttpPort = 10809,
    // Whether the local proxy admits a client without an account.
    bool ProxyAnonymous = false,
    // Accounts the local proxy admits clients under, one "user:password" per line.
    string ProxyCredentials = "",
    // Whether the listener is up; false while enabled means it could not bind.
    bool ProxyRunning = false,
    // Why the local proxy is not listening; empty while it holds.
    string ProxyError = "",
    // Addresses other machines reach the proxy at, the ones on a routed link first; empty while it is not listening.
    IReadOnlyList<string>? ProxyAddresses = null,
    // Clients holding a connection to the local proxy right now.
    IReadOnlyList<ProxyClientEntry>? ProxyClients = null,
    // Whether the resolver this machine sends its lookups to stopped answering, so rules by domain no longer apply.
    bool DnsUnreachable = false,
    // How the tunnel reaches other devices: lan, wifi, or both.
    string ShareMode = ShareModes.Default,
    // Whether a wired subnet is asked for; the gateway behind it is not built yet.
    bool ShareEthernet = false,
    // Whether this machine can raise an access point.
    bool HotspotSupported = false,
    // Why it cannot (HotspotReasons token); empty while it can.
    string HotspotReason = HotspotReasons.Ready,
    // Whether the access point is up.
    bool HotspotRunning = false,
    // Why the access point is down; empty while it holds.
    string HotspotError = "",
    // Network name of the access point.
    string HotspotSsid = "",
    // Password of the access point.
    string HotspotPassword = "",
    // Band asked for: auto, 2.4, or 5.
    string HotspotBand = HotspotBands.Auto,
    // Band the access point took; differs from the one asked for when the adapter already holds a channel.
    string HotspotBandActual = "",
    // Devices on the access point right now.
    int HotspotClients = 0,
    // How many devices the access point admits.
    int HotspotMaxClients = 0,
    // Whether subscriptions are re-read on a timer.
    bool SubscriptionAutoRefresh = true,
    // How often subscriptions are re-read when the panel names no interval of its own, in hours.
    int SubscriptionRefreshIntervalHours = 12,
    // Whether several tunnels may be up at once.
    bool MultiServer = false,
    // What the mode holds while several tunnels may be up: every server, its role and what it carries. Null on a
    // machine that keeps one tunnel.
    FleetSnapshot? Fleet = null);
