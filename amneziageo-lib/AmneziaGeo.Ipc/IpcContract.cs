namespace AmneziaGeo.Ipc;

/// <summary>
/// Shared constants for the agent status/control pipe protocol.
/// </summary>
public static class IpcContract
{
    /// <summary>
    /// The named-pipe name the agent listens on.
    /// </summary>
    public const string PipeName = "AmneziaGeo.Agent";

    /// <summary>
    /// Envelope type for a client greeting.
    /// </summary>
    public const string HelloType = "hello";

    /// <summary>
    /// Envelope type for a status snapshot pushed by the agent.
    /// </summary>
    public const string SnapshotType = "snapshot";

    /// <summary>
    /// Envelope type for a command sent by the UI.
    /// </summary>
    public const string CommandType = "command";

    /// <summary>
    /// Envelope type for the agent's reply to a command.
    /// </summary>
    public const string AckType = "ack";

    /// <summary>
    /// Command to import a configuration: args are name and source file path.
    /// </summary>
    public const string OpAddConfig = "add-config";

    /// <summary>
    /// Command to set a config's geo split-tunnel settings: args are name, on/off, then rule tokens.
    /// </summary>
    public const string OpSetGeo = "set-geo";

    /// <summary>
    /// Command to set a config's WebSocket transport, tunnel MTU override, and IPv6 opt-in. Args: name, on/off,
    /// port, optional host, optional mtu, optional ipv6 (on/off; absent keeps the stored value). Applies on the
    /// next connect.
    /// </summary>
    public const string OpSetWebSocket = "set-websocket";

    /// <summary>
    /// Command to set a config's preferred DNS for local name resolution. Args: name, servers
    /// (comma/space-separated; empty clears it). Applies on the next connect.
    /// </summary>
    public const string OpSetConfigDns = "set-config-dns";

    /// <summary>
    /// Command to set a config's bypass exclusions. Args: name, exclusions (one entry per line /
    /// comma-separated; domains kept on the local resolver, IP/CIDR routed direct). Applies on the next
    /// connect.
    /// </summary>
    public const string OpSetConfigExclusions = "set-config-exclusions";

    /// <summary>
    /// Command to list the machine's currently-connected local subnets. No args. The ack message holds
    /// newline-separated CIDRs; the UI merges them into a config's exclusions list on demand.
    /// </summary>
    public const string OpListLocalSubnets = "list-local-subnets";

    /// <summary>
    /// Command to list available geo categories; the ack message holds newline-separated rule tokens.
    /// </summary>
    public const string OpListGeo = "list-geo";

    /// <summary>
    /// Command to read the entries a geo rule expands to. Args: [0] rule token ("geosite:github" / "geoip:ru");
    /// [1] optional cap on returned entries (default 300, clamped 1..5000). The ack message holds a JSON object
    /// { total, entries } where total counts the whole category and entries carries the capped preview.
    /// </summary>
    public const string OpGetGeoEntries = "get-geo-entries";

    /// <summary>
    /// Command to list running applications and services for the per-app tunneling picker. The ack
    /// message holds newline-separated rows, each tab-separated: kind ("app"/"service"), label, value,
    /// detail.
    /// </summary>
    public const string OpListProcesses = "list-processes";

    /// <summary>
    /// Command to add or update a routing list. Args: id (0 to insert), name, then role-tagged rule tokens
    /// ("proxy|geosite:x", "direct|geoip:ru", "block|domain:y"; a bare token defaults to proxy).
    /// The ack message holds the resulting id.
    /// </summary>
    public const string OpSaveRoutingList = "save-routing-list";

    /// <summary>
    /// Command to remove a routing list by id. Args: id.
    /// </summary>
    public const string OpRemoveRoutingList = "remove-routing-list";

    /// <summary>
    /// Command to set the order the routing lists are listed in. Args: the names, in the order they are shown.
    /// </summary>
    public const string OpReorderRoutingLists = "reorder-routing-lists";

    /// <summary>
    /// Command to fetch a routing list's full rules. Args: id. The ack message holds newline-separated
    /// role-tagged rule tokens ("proxy|geosite:openai", "block|domain:x" etc).
    /// </summary>
    public const string OpGetRoutingList = "get-routing-list";

    /// <summary>
    /// Command to count the routes a rule set would put into the tun. Args: [0] mode ("full" / "split"), then
    /// role-tagged rule tokens. The ack message holds a JSON object { routes, limit }; limit 0 means the device
    /// carries any number of them.
    /// </summary>
    public const string OpCountRoutes = "count-routes";

    /// <summary>
    /// Command to pick the routing list every config uses. Args: list id, or "none" to turn routing off.
    /// </summary>
    public const string OpAssignRouting = "assign-routing";

    /// <summary>
    /// Command to set a routing list's traffic settings. Args: routing list id, exclusions (one entry per
    /// line / comma-separated), all-UDP ("on"/"off"), mode ("split"/"full", derived from global-proxy),
    /// use-global-proxy ("on"/"off"). An all-default tuple clears the row. Applies on the next connect.
    /// IPv6 is per-config now (set-websocket), no longer carried here.
    /// </summary>
    public const string OpSetRoutingSettings = "set-routing-settings";

    /// <summary>
    /// Command to fetch a routing list's traffic settings. Args: routing list id. The ack message holds a
    /// JSON object { exclusions, allUdp, mode, useGlobalProxy } (defaults when no row is stored).
    /// </summary>
    public const string OpGetRoutingSettings = "get-routing-settings";

    /// <summary>
    /// Command to set the agent's desired connection state. Args: "connect" or "disconnect".
    /// </summary>
    public const string OpSetConnection = "set-connection";

    /// <summary>
    /// Command to set a named agent setting. Args: key, value. Used for the kill-switch and LAN toggles.
    /// </summary>
    public const string OpSetSetting = "set-setting";

    /// <summary>
    /// Command to choose the config the agent binds to. Args: name.
    /// If connected, the agent switches to it; otherwise it becomes the target the next connect uses.
    /// </summary>
    public const string OpSelectConfig = "set-config";

    /// <summary>
    /// Adds a subscription and reads it right away: address, and optionally a name.
    /// </summary>
    public const string OpAddSubscription = "add-subscription";

    /// <summary>
    /// Lists the subscriptions as a JSON array of entries.
    /// </summary>
    public const string OpListSubscriptions = "list-subscriptions";

    /// <summary>
    /// Re-reads one subscription by name, or every one of them when none is named.
    /// </summary>
    public const string OpRefreshSubscription = "refresh-subscription";

    /// <summary>
    /// Drops a subscription; the second argument "configs" drops what it brought in as well.
    /// </summary>
    public const string OpRemoveSubscription = "remove-subscription";

    /// <summary>
    /// Returns the address of the subscription a configuration came from, empty when it came from elsewhere. Args: name.
    /// </summary>
    public const string OpConfigSubscription = "config-subscription";

    /// <summary>
    /// Command to add a geo data source and download it immediately. Args: kind (geosite/geoip), url.
    /// </summary>
    public const string OpAddSource = "add-source";

    /// <summary>
    /// Command to remove a geo data source (and its downloaded file) by name. Args: name.
    /// </summary>
    public const string OpRemoveSource = "remove-source";

    /// <summary>
    /// Command to edit a geo data source's kind and url in place, keeping its opaque name. Args: name,
    /// kind (geosite/geoip), url. On a url change the cached file is dropped so the new url re-downloads
    /// unconditionally; the source is then re-downloaded and re-materialized.
    /// </summary>
    public const string OpEditSource = "edit-source";

    /// <summary>
    /// Command to re-download every geo data source and re-materialize the routing lists. No args.
    /// </summary>
    public const string OpUpdateSources = "update-sources";

    /// <summary>
    /// Command to re-download a single geo data source by name and re-materialize the routing lists. Args: name.
    /// </summary>
    public const string OpUpdateSource = "update-source";

    /// <summary>
    /// Command to check every geo data source for a newer remote file WITHOUT downloading it (conditional
    /// request / checksum). No args. Each source's result rides the next snapshot
    /// (SourceEntry.UpdateAvailable); the ack message holds a human-readable summary.
    /// </summary>
    public const string OpCheckSources = "check-sources";

    /// <summary>
    /// Re-reads the geo sources and their file state from the store and pushes a snapshot. No args. Sent when
    /// the settings screen opens: the state lives in the store, not on the agent's heap.
    /// </summary>
    public const string OpRefreshSources = "refresh-sources";

    /// <summary>
    /// Command to check a single geo data source for a newer remote file without downloading it. Args: name.
    /// The result rides the next snapshot; the ack message holds a human-readable status.
    /// </summary>
    public const string OpCheckSource = "check-source";

    /// <summary>
    /// Command to read a stored config's wg-quick text for export. Args: name. The ack message holds the
    /// raw .conf text.
    /// </summary>
    public const string OpGetConfig = "get-config";

    /// <summary>
    /// Command to import a config from raw wg-quick text (file/QR/link parsed UI-side). Args: name, text.
    /// </summary>
    public const string OpImportConfig = "import-config";

    /// <summary>
    /// Command to overwrite an existing config's wg-quick text (manual edit). Args: name, text. The config
    /// must already exist; its geo and routing state are preserved.
    /// </summary>
    public const string OpEditConfig = "edit-config";

    /// <summary>
    /// Command to delete a stored config by name, with its service, geo settings and resolutions. Args: name.
    /// Refused if the config is the running one.
    /// </summary>
    public const string OpRemoveConfig = "remove-config";

    /// <summary>
    /// Command to rename a config. Args: current name, new name. Carries the config's geo, transport,
    /// resolutions and the agent's selection across. Refused if in use by the running tunnel.
    /// </summary>
    public const string OpRenameConfig = "rename-config";

    /// <summary>
    /// Command to set the order the configs are listed in. Args: the names, in the order they are shown.
    /// </summary>
    public const string OpReorderConfigs = "reorder-configs";

    /// <summary>
    /// Command to duplicate a config into an independent copy. Args: source name, destination name. Copies
    /// the config text plus its geo settings and cached resolutions; the destination must be a free name.
    /// </summary>
    public const string OpCopyConfig = "copy-config";

    /// <summary>
    /// Command to export a selective bundle of configs and routing lists as a portable JSON file.
    /// Args: a selection JSON object (each array optional). The ack message holds the bundle JSON.
    /// </summary>
    public const string OpExportBundle = "export-bundle";

    /// <summary>
    /// Command to import a selective bundle, recreating its configs and routing lists as new, independent
    /// entities under fresh (de-duplicated) names on any name collision. Args: bundle json.
    /// The ack message holds a human-readable summary.
    /// </summary>
    public const string OpImportBundle = "import-bundle";

    /// <summary>
    /// Command to check for an application update against the configured update URL. No args. The ack
    /// message holds a human-readable status; availability also rides the next status snapshot.
    /// </summary>
    public const string OpCheckUpdate = "check-update";

    /// <summary>
    /// Command for the UI process that owns the setup download to report its phase to the agent, so the tray
    /// and every window share one download state. Args: [0] phase ("idle" / "downloading" / "downloaded"),
    /// [1] percent (0..100), [2] setup path (set when downloaded), [3] version the setup carries. The phase
    /// rides the next status snapshot (UpdateDownloading / UpdateDownloaded / UpdateDownloadPercent /
    /// UpdateSetupPath).
    /// </summary>
    public const string OpReportUpdateDownload = "report-update-download";

    /// <summary>
    /// Command to cancel a running setup download. No args. The agent flags the request on the update state so
    /// it rides the next snapshot (UpdateCancelRequested), and the UI process that owns the byte-pump aborts it.
    /// </summary>
    public const string OpCancelUpdateDownload = "cancel-download";

    /// <summary>
    /// Command to download the update the last check resolved. No args. Used where the agent owns the
    /// download (Linux packages); the phase rides the next status snapshot.
    /// </summary>
    public const string OpDownloadUpdate = "download-update";

    /// <summary>
    /// Command to install the downloaded update. No args. Used where the agent owns the install (Linux
    /// packages): it verifies the packages and hands them to the system package manager.
    /// </summary>
    public const string OpApplyUpdate = "apply-update";

    /// <summary>
    /// Command to seed the default geo sources (if none) and synchronously download every source and
    /// re-materialize the routing lists. No args. Used by the installer's "download lists" step; the ack
    /// returns a human-readable result and Ok=false on any download failure (non-fatal to the caller).
    /// </summary>
    public const string OpDownloadGeo = "download-geo";

    /// <summary>
    /// Command to build a redacted diagnostics bundle for support. No args. The agent zips the log files
    /// from both processes plus a summary and the live journal (secrets scrubbed) and returns the full path
    /// to the written .zip in the ack message; Ok=false with the reason on failure.
    /// </summary>
    public const string OpCollectDiagnostics = "collect-diagnostics";

    /// <summary>
    /// Command to run the channel check: the ladder from the local gateway out to a download through the tunnel.
    /// Optional arg [0]: a host or URL timed over the same tunnel as the neutral download, so the two separate a
    /// slow source from a slow tunnel; absent, the agent uses the destination its relay sees carrying the most
    /// traffic and skips that leg when it knows of none. The ack message holds one tab-separated "leg" row per
    /// measured leg and a closing "verdict" row
    /// naming the culprit. The run is stored in the check journal as well, so it travels in the diagnostics
    /// archive whatever the log is set to capture.
    /// </summary>
    public const string OpCheckChannel = "check-channel";

    /// <summary>
    /// Command to measure every saved server with the light legs: an echo burst to each endpoint, or a connect
    /// burst to its websocket front. No args. The ack message holds one "leg" row for the local gateway, one
    /// tab-separated "srv" row per server and a closing "verdict" row naming the one to be on. The run is stored
    /// in the check journal as well. Nothing is downloaded here: a rate rides one tunnel at a time.
    /// </summary>
    public const string OpCheckServers = "check-servers";

    /// <summary>
    /// Command to check one destination: args are a target token - a domain, an address, "app:pkg=..." /
    /// "app:path=..." or a geo rule ("geosite:telegram"). The ack message holds tab-separated "fact" rows and a
    /// closing "verdict" row saying why that traffic goes where it goes. Stored in the check journal too.
    /// </summary>
    public const string OpCheckTarget = "check-target";

    /// <summary>
    /// Command to measure one destination: args are [0] the target - a domain or an address; [1] the path to
    /// measure it over - "auto" (leave the routing alone), "tunnel" (force the target through the tunnel) or
    /// "bypass" (force it past the tunnel); [2] optional URL of the service the send leg uploads to (empty =
    /// the built-in one). The ack message holds tab-separated "leg" rows and a closing "verdict" row. The run
    /// is stored in the probe journal, which travels in the diagnostics archive on its own.
    /// </summary>
    public const string OpProbeTarget = "probe-target";

    /// <summary>
    /// Command to read a window of one log table for the in-app viewer. Args: [0] table ("ageo"/"routes"/"checks");
    /// [1] optional limit (rows, default 400, clamped 1..2000); [2] optional beforeId cursor (read rows with
    /// id below it to page older, omitted/0 = live tail); [3] optional level token (ageo: hide rows less
    /// severe than it); [4] optional search substring (matches message or source). The ack message holds a
    /// JSON object { lines: string[] (rendered, newest first), firstId: long (smallest id in the window),
    /// hasOlder: bool, matchCount: int (total matches when searching) }.
    /// </summary>
    public const string OpReadLog = "read-log";

    /// <summary>
    /// Command to clear one log table. Args: [0] table ("ageo"/"routes"/"checks"). Other logs are left untouched.
    /// </summary>
    public const string OpClearLog = "clear-log";

    /// <summary>
    /// Command to render a whole log table to text for the UI to save. Args: [0] table ("ageo"/"routes"/"checks").
    /// The agent renders every row and returns the text in the ack message; the UI writes the file under the
    /// user account. The agent never writes a caller-supplied path.
    /// </summary>
    public const string OpExportLog = "export-log";

    /// <summary>
    /// Command to render the configuration the agent runs on, or would run on at the next connect. No args.
    /// The ack message holds the rendered report; keys are masked.
    /// </summary>
    public const string OpGetRuntimeConfig = "get-runtime-config";

    /// <summary>
    /// Command to read what the tunnel decides for right now. No args. The ack message holds the session report:
    /// one "session" row per address, carrying the name it was resolved by, the path it takes, what settled it and
    /// how long it is held, then a closing "held" row with the totals, the mode and the routing list in force.
    /// Where nothing relays connections a row counts no bytes.
    /// </summary>
    public const string OpGetSessions = "get-sessions";

    /// <summary>
    /// Command to read every destination the agent can put a name to: what it resolved for the selected config
    /// before, which outlives a disconnect, and what the tunnel carries right now. No args. The ack message
    /// holds one name per line.
    /// </summary>
    public const string OpKnownHosts = "known-hosts";

    /// <summary>
    /// Command for the UI to record a diagnostic line in the agent log (the UI process keeps no log of its
    /// own). Args: [0] message. Logged at warning level.
    /// </summary>
    public const string OpLogClient = "log-client";

    /// <summary>
    /// Sent once by the UI to mark its pipe connection as a presence-holding session. No args. The agent
    /// ties the tunnel's lifetime to UI presence and disconnects after a short grace when the last UI
    /// session drops. Transient command clients never send this.
    /// </summary>
    public const string OpAttachUi = "attach-ui";

    /// <summary>
    /// Command to open the system VPN settings, where the user switches always-on on. No args. Android only:
    /// no application may set always-on for itself.
    /// </summary>
    public const string OpOpenVpnSettings = "open-vpn-settings";
}
