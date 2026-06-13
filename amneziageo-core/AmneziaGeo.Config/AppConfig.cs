namespace AmneziaGeo.Config;

/// <summary>
/// Application configuration.
/// </summary>
public sealed record AppConfig
{
    /// <summary>
    /// Path to the SQLite state database.
    /// </summary>
    public string DatabasePath { get; init; } = "amneziageo.db";

    /// <summary>
    /// Name of the active profile, if any.
    /// </summary>
    public string? ActiveProfile { get; init; }

    /// <summary>
    /// Whether geo split tunneling is enabled.
    /// </summary>
    public bool GeoSplitTunnel { get; init; }
}
