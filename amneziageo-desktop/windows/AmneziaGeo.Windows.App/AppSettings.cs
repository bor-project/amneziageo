using System.Linq;
using System.Reflection;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Runtime tuning settings persisted in the state database.
/// </summary>
internal sealed record AppSettings
{
    // The update URL baked into the assembly at build time (App.csproj AssemblyMetadata, fed from
    // installer.config.json by build-installer.ps1). Empty when the build configured no update URL.
    private static readonly string BakedUpdateUrl =
        Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "AmneziaGeo.UpdateUrl")?.Value ?? string.Empty;

    // The AmneziaWG engine version baked into the assembly at build time (App.csproj runs `git describe`
    // on the bundled amneziawg-windows submodule). tunnel.dll has no version resource, so this is the
    // authoritative engine version. Empty when the build could not resolve it (no git / no submodule).
    private static readonly string BakedEngineVersion =
        Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "AmneziaGeo.EngineVersion")?.Value?.Trim() ?? string.Empty;

    /// <summary>
    /// AmneziaWG engine (tunnel.dll) version, baked from the amneziawg-windows submodule at build time.
    /// A build constant (not persisted), exposed next to the other baked-in value (the update URL).
    /// Empty if the build could not resolve it.
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
    /// URL of the update metadata file (JSON with version/description/setup). Empty disables update
    /// checks (and hides their UI). The installer is expected to sit next to this file (resolved relative
    /// to it). Defaults to the value baked into the build from installer.config.json.
    /// </summary>
    public string UpdateUrl { get; init; } = BakedUpdateUrl;

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
    /// When set (and the tunnel is in split mode), every outbound UDP datagram's destination is routed
    /// through the tunnel - a catch-all for real-time media (e.g. Discord voice) whose server IPs arrive via
    /// app-layer signaling, not DNS, so split rules never capture them. Off by default. Implemented by the
    /// ETW UDP tracker running WITHOUT its per-app PID filter; the tunnel's own underlay endpoint is excluded
    /// so the WG transport never loops back into the tunnel.
    /// </summary>
    public bool TunnelAllUdp { get; init; }
}
