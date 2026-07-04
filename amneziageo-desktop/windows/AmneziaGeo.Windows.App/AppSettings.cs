using System.Linq;
using System.Reflection;

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

    /// <summary>
    /// Engine version baked at build time.
    /// </summary>
    public static string EngineVersion => BakedEngineVersion;

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
    /// Log verbosity token: info, debug, or trace.
    /// </summary>
    public string LogLevel { get; init; } = "info";

    /// <summary>
    /// Whether the dedicated routing log is recording.
    /// </summary>
    public bool RouteLog { get; init; }
}
