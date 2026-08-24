using System.Linq;
using System.Reflection;
using AmneziaGeo.Routing;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Runtime tuning settings persisted in the state database.
/// </summary>
internal sealed record AppSettings
{
    // Baked from installer.config.json at build time.
    private static readonly string BakedUpdateUrl =
        Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "AmneziaGeo.UpdateUrl")?.Value ?? string.Empty;

    // Engine version from git describe on the amneziawg-windows submodule.
    private static readonly string BakedEngineVersion =
        Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "AmneziaGeo.EngineVersion")?.Value?.Trim() ?? string.Empty;

    // Build target (win-<arch>-<fdd|scd>) baked at build time; the update check builds the per-build
    // installer name from it.
    private static readonly string BakedBuildTarget =
        Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "AmneziaGeo.BuildTarget")?.Value?.Trim() ?? string.Empty;

    // Prerelease channel default baked from installer.config.json (allowPrerelease); "1" seeds the runtime
    // toggle on. Once the user flips it in settings, the persisted value wins over this default.
    private static readonly bool BakedAllowPrerelease =
        string.Equals(
            Assembly.GetExecutingAssembly()
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "AmneziaGeo.AllowPrerelease")?.Value?.Trim(),
            "1", StringComparison.Ordinal);

    /// <summary>
    /// Engine version baked at build time.
    /// </summary>
    public static string EngineVersion => BakedEngineVersion;

    /// <summary>
    /// Build target (win-<arch>-<fdd|scd>) baked at build time.
    /// </summary>
    public static string BuildTarget => BakedBuildTarget;

    /// <summary>
    /// Whether the update check offers prereleases; defaults to the baked channel, then user-toggleable.
    /// </summary>
    public bool AllowPrerelease { get; init; } = BakedAllowPrerelease;

    /// <summary>
    /// Setting key the route lifetime persists under.
    /// </summary>
    public const string RouteTtlKey = AmneziaGeo.Ipc.SettingKeys.RouteTtl;

    /// <summary>
    /// How long a routed destination survives without traffic before its route and filters are reclaimed, in seconds.
    /// </summary>
    public int RouteTtlSeconds { get; init; } = 300;

    /// <summary>
    /// How long a connect attempt waits for a handshake before declaring the server unreachable, in seconds.
    /// </summary>
    public int ConnectTimeoutSeconds { get; init; } = 20;

    /// <summary>
    /// Handshake age beyond which a connected tunnel is treated as dead (triggering a re-dial), in seconds.
    /// </summary>
    public int DeadThresholdSeconds { get; init; } = 180;

    /// <summary>
    /// Update metadata URL.
    /// </summary>
    public string UpdateUrl { get; init; } = BakedUpdateUrl;

    /// <summary>
    /// Periodic geo-source update check.
    /// </summary>
    public bool GeoAutoCheck { get; init; } = true;

    /// <summary>
    /// How often the periodic geo-source update-check runs, in hours.
    /// </summary>
    public int GeoCheckIntervalHours { get; init; } = 24;

    /// <summary>
    /// Geo address cache validity, in hours.
    /// </summary>
    public int GeoCacheValidityHours { get; init; } = 24;

    /// <summary>
    /// Route all outbound UDP through the tunnel in split mode.
    /// </summary>
    public bool TunnelAllUdp { get; init; }

    /// <summary>
    /// Log verbosity token: error, info, debug, or trace.
    /// </summary>
    public string LogLevel { get; init; } = LogLevelController.DefaultToken;

    /// <summary>
    /// Whether the dedicated routing log is recording.
    /// </summary>
    public bool RouteLog { get; init; }

    /// <summary>
    /// Auto-connect the selected profile on service start (survive a reboot).
    /// </summary>
    public bool SurviveReboot { get; init; }

    /// <summary>
    /// Retry a desired connection at a fixed interval instead of the default backoff.
    /// </summary>
    public bool PeriodicReconnect { get; init; }

    /// <summary>
    /// Interval between periodic auto-reconnect attempts, in seconds.
    /// </summary>
    public int PeriodicReconnectIntervalSeconds { get; init; } = 30;

    /// <summary>
    /// Whether the default route moves to the next server when the one carrying it stops answering.
    /// </summary>
    public bool FailoverEnabled { get; init; }

    /// <summary>
    /// Minutes a higher-priority server answers before the default route goes back to it; 0 leaves it where it is.
    /// </summary>
    public int FailoverReturnMinutes { get; init; }

    /// <summary>
    /// Configurations auto-switching passes over, one name per line.
    /// </summary>
    public string FailoverSkipped { get; init; } = string.Empty;

    /// <summary>
    /// Show tray notifications for connection state changes.
    /// </summary>
    public bool ShowNotifications { get; init; } = true;

    /// <summary>
    /// Whether the local proxy listens.
    /// </summary>
    public bool ProxyEnabled { get; init; }

    /// <summary>
    /// SOCKS5 port of the local proxy.
    /// </summary>
    public int ProxySocksPort { get; init; } = LocalProxyOptions.DefaultSocksPort;

    /// <summary>
    /// HTTP port of the local proxy.
    /// </summary>
    public int ProxyHttpPort { get; init; } = LocalProxyOptions.DefaultHttpPort;

    /// <summary>
    /// Whether the local proxy admits a client without an account.
    /// </summary>
    public bool ProxyAnonymous { get; init; }

    /// <summary>
    /// Accounts the local proxy admits clients under, one "user:password" per line.
    /// </summary>
    public string ProxyCredentials { get; init; } = string.Empty;

    /// <summary>
    /// The local proxy as the listener takes it.
    /// </summary>
    public LocalProxyOptions Proxy()
    {
        return new LocalProxyOptions
        {
            Enabled = ProxyEnabled,
            SocksPort = ProxySocksPort,
            HttpPort = ProxyHttpPort,
            AllowAnonymous = ProxyAnonymous,
            Credentials = ProxyCredentials,
        };
    }
}
