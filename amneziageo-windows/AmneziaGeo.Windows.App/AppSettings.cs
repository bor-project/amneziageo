namespace AmneziaGeo.Windows.App;

/// <summary>
/// Runtime tuning settings persisted in the state database.
/// </summary>
internal sealed record AppSettings
{
    /// <summary>
    /// How often tunneled domains are re-resolved, in seconds.
    /// </summary>
    public int RefreshSeconds { get; init; } = 60;

    /// <summary>
    /// How long a connect attempt waits for a handshake before declaring the server unreachable, in seconds.
    /// </summary>
    public int ConnectTimeoutSeconds { get; init; } = 20;

    /// <summary>
    /// Handshake age beyond which a connected tunnel is treated as dead (triggering a re-dial), in seconds.
    /// </summary>
    public int DeadThresholdSeconds { get; init; } = 180;

    /// <summary>
    /// URL of the update metadata file (JSON with version/description/setup). Empty disables update
    /// checks. The installer is expected to sit next to this file (resolved relative to it).
    /// </summary>
    public string UpdateUrl { get; init; } = string.Empty;

    /// <summary>
    /// When set, the agent periodically checks the geo sources for a newer remote file (without
    /// downloading) and the UI badges/notifies. Defaults on.
    /// </summary>
    public bool GeoAutoCheck { get; init; } = true;

    /// <summary>
    /// How often the periodic geo-source update-check runs, in hours.
    /// </summary>
    public int GeoCheckIntervalHours { get; init; } = 24;

    /// <summary>
    /// Preferred DNS server(s) for NON-tunneled (local) name resolution, comma/space separated. Empty
    /// means auto-detect the system's current resolvers. Tunneled (geo-matched) names still use the
    /// config's clean resolver, so this does not weaken geo resolution.
    /// </summary>
    public string PreferredDns { get; init; } = string.Empty;

    /// <summary>
    /// User bypass list, one entry per line (or comma/semicolon separated): a domain suffix
    /// (e.g. <c>.ddns.example.net</c>, <c>corp.local</c>) kept on the LOCAL resolver, or an IP/CIDR
    /// (e.g. <c>192.168.50.0/24</c>) routed straight out the physical gateway instead of the tunnel.
    /// Always combined with the built-in defaults (loopback, RFC1918 LAN, common local suffixes); empty
    /// means just the defaults. Lets the local network and chosen hosts stay reachable in full tunnel.
    /// </summary>
    public string Exclusions { get; init; } = string.Empty;

    /// <summary>
    /// When set, every connect also auto-detects the machine's currently-connected local subnets (the
    /// physical adapters' on-link IPv4 networks not already covered by the built-in RFC1918 defaults) and
    /// keeps them direct — so non-RFC1918 corporate / CGNAT LANs are ignored by the tunnel without manual
    /// entry. Re-detected each connect (picks up DHCP / roaming changes) and de-duplicated against the
    /// defaults and the manual list. Default on.
    /// </summary>
    public bool AutoExcludeLan { get; init; } = true;
}
