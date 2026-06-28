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
    /// Command to save a profile: args are name and its single configuration name
    /// (an empty configuration name leaves the profile without a configuration).
    /// </summary>
    public const string OpAddBalancer = "add-balancer";

    /// <summary>
    /// Command to set a config's geo split-tunnel settings: args are name, on/off, then rule tokens.
    /// </summary>
    public const string OpSetGeo = "set-geo";

    /// <summary>
    /// Command to set a config's WebSocket transport (carry the AmneziaWG UDP over TCP/TLS via wstunnel,
    /// for networks that block UDP). Args: name, on/off, port (the wstunnel server's TLS port, e.g. 443),
    /// and optional host (the wstunnel server hostname; empty reuses the config's own Endpoint host).
    /// Applies on the next connect.
    /// </summary>
    public const string OpSetWebSocket = "set-websocket";

    /// <summary>
    /// Command to set a config's preferred DNS for NON-tunneled (local) name resolution. Args: name,
    /// servers (comma/space-separated; empty clears it → auto-detect the system resolvers). Applies on the
    /// next connect. Moved here from the former global "preferred-dns" app setting.
    /// </summary>
    public const string OpSetConfigDns = "set-config-dns";

    /// <summary>
    /// Command to set a config's bypass exclusions (kept OFF the tunnel). Args: name, exclusions (one entry
    /// per line / comma-separated; domains kept on the local resolver, IP/CIDR routed direct). Applies on
    /// the next connect. Moved here from the former global "exclusions" app setting.
    /// </summary>
    public const string OpSetConfigExclusions = "set-config-exclusions";

    /// <summary>
    /// Command to list the machine's currently-connected local subnets (the non-RFC1918 / CGNAT networks the
    /// built-in defaults miss). No args. The ack message holds newline-separated CIDRs; the UI merges them
    /// into a profile's exclusions list on demand (replacing the former auto-exclude-LAN flag).
    /// </summary>
    public const string OpListLocalSubnets = "list-local-subnets";

    /// <summary>
    /// Command to list available geo categories; the ack message holds newline-separated rule tokens.
    /// </summary>
    public const string OpListGeo = "list-geo";

    /// <summary>
    /// Command to list running applications and services for the per-app tunneling picker (#68). The ack
    /// message holds newline-separated rows, each tab-separated: kind ("app"/"service"), label, value
    /// (exe path for an app, service name for a service), detail (host exe path for a service, else empty).
    /// </summary>
    public const string OpListProcesses = "list-processes";

    /// <summary>
    /// Command to add or update a routing list. Args: id (0 to insert), name, then rule tokens.
    /// The ack message holds the resulting id.
    /// </summary>
    public const string OpSaveRoutingList = "save-routing-list";

    /// <summary>
    /// Command to remove a routing list by id. Args: id.
    /// </summary>
    public const string OpRemoveRoutingList = "remove-routing-list";

    /// <summary>
    /// Command to fetch a routing list's full rules. Args: id. The ack message holds
    /// newline-separated rule tokens (geosite:openai etc).
    /// </summary>
    public const string OpGetRoutingList = "get-routing-list";

    /// <summary>
    /// Command to assign or unassign a routing list to a profile and toggle its use.
    /// Args: profile name, list id (or "none"), "on" / "off".
    /// </summary>
    public const string OpAssignRouting = "assign-routing";

    /// <summary>
    /// Command to set the agent's desired connection state. Args: "connect" or "disconnect".
    /// </summary>
    public const string OpSetConnection = "set-connection";

    /// <summary>
    /// Command to set a named agent setting. Args: key, value. Used for the kill-switch and LAN toggles.
    /// </summary>
    public const string OpSetSetting = "set-setting";

    /// <summary>
    /// Command to choose the active profile (balancer or single config) the agent binds to. Args: name.
    /// If connected, the agent switches to it; otherwise it becomes the target the next connect uses.
    /// </summary>
    public const string OpSelectProfile = "set-profile";

    /// <summary>
    /// Command to add a geo data source and download it immediately. Args: kind (geosite/geoip), url.
    /// </summary>
    public const string OpAddSource = "add-source";

    /// <summary>
    /// Command to remove a geo data source (and its downloaded file) by name. Args: name.
    /// </summary>
    public const string OpRemoveSource = "remove-source";

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
    /// must already exist; its profile memberships, geo, and routing state are preserved.
    /// </summary>
    public const string OpEditConfig = "edit-config";

    /// <summary>
    /// Command to delete a stored config by name, with its service, geo settings, resolutions, and
    /// balancer memberships. Args: name. Refused if the config is a member of the running profile.
    /// </summary>
    public const string OpRemoveConfig = "remove-config";

    /// <summary>
    /// Command to delete a profile (balancer) by name. Args: name. Refused if it is the running profile.
    /// </summary>
    public const string OpRemoveBalancer = "remove-balancer";

    /// <summary>
    /// Command to rename a config. Args: current name, new name. Carries the config's geo, transport,
    /// resolutions, and balancer memberships across. Refused if in use by the running tunnel.
    /// </summary>
    public const string OpRenameConfig = "rename-config";

    /// <summary>
    /// Command to duplicate a config into an independent copy. Args: source name, destination name. Copies
    /// the config text plus its geo settings and cached resolutions; the destination must be a free name.
    /// </summary>
    public const string OpCopyConfig = "copy-config";

    /// <summary>
    /// Command to rename a profile (balancer). Args: current name, new name. Carries the profile's
    /// routing assignment and selection/binding across. Refused if it is the running profile.
    /// </summary>
    public const string OpRenameProfile = "rename-profile";

    /// <summary>
    /// Command to export a profile as a portable, self-contained JSON bundle: its config (.conf text with
    /// keys), transport, the config's own geo split, and its routing list. Args: profile name. The ack
    /// message holds the JSON.
    /// </summary>
    public const string OpExportProfile = "export-profile";

    /// <summary>
    /// Command to import a profile from a portable JSON bundle, recreating its config, transport, geo, and
    /// routing list as new, independent entities under fresh (de-duplicated) names. Args: bundle json. The
    /// ack message holds the new profile name.
    /// </summary>
    public const string OpImportProfile = "import-profile";

    /// <summary>
    /// Command to check for an application update against the configured update URL. No args. The ack
    /// message holds a human-readable status; availability also rides the next status snapshot.
    /// </summary>
    public const string OpCheckUpdate = "check-update";

    /// <summary>
    /// Command to seed the default geo sources (if none) and synchronously download every source and
    /// re-materialize the routing lists. No args. Used by the installer's "download lists" step; the ack
    /// returns a human-readable result and Ok=false on any download failure (non-fatal to the caller).
    /// </summary>
    public const string OpDownloadGeo = "download-geo";

    /// <summary>
    /// Sent once by the UI right after connecting to mark its pipe connection as a presence-holding
    /// session. No args. The agent ties the tunnel's lifetime to UI presence: when the last attached UI
    /// session drops (window closed or the process crashed) and a tunnel is up, the agent disconnects it
    /// after a short grace. Transient command clients (the CLI) never send this, so they do not keep the
    /// tunnel alive or tear it down.
    /// </summary>
    public const string OpAttachUi = "attach-ui";
}
