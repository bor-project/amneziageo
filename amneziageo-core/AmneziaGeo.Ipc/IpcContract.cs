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
    /// Command to save a balancer: args are name, recheck seconds, mode, then member names.
    /// </summary>
    public const string OpAddBalancer = "add-balancer";

    /// <summary>
    /// Command to set a config's geo split-tunnel settings: args are name, on/off, then rule tokens.
    /// </summary>
    public const string OpSetGeo = "set-geo";

    /// <summary>
    /// Command to list available geo categories; the ack message holds newline-separated rule tokens.
    /// </summary>
    public const string OpListGeo = "list-geo";

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
    /// Command to read a stored config's wg-quick text for export. Args: name. The ack message holds the
    /// raw .conf text.
    /// </summary>
    public const string OpGetConfig = "get-config";

    /// <summary>
    /// Command to import a config from raw wg-quick text (file/QR/link parsed UI-side). Args: name, text.
    /// </summary>
    public const string OpImportConfig = "import-config";
}
