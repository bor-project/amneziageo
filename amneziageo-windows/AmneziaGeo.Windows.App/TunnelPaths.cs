namespace AmneziaGeo.Windows.App;

/// <summary>
/// File and service naming for tunnels.
/// </summary>
internal static class TunnelPaths
{
    /// <summary>
    /// Windows service name for a tunnel.
    /// </summary>
    public static string ServiceName(string name)
    {
        return $"AmneziaGeo${name}";
    }

    /// <summary>
    /// Path to the stored wg-quick config for a tunnel.
    /// </summary>
    public static string ConfigFile(string name)
    {
        return Path.Combine(RootDirectory(), "Configurations", $"{name}.conf");
    }

    /// <summary>
    /// Path to a downloaded geo database file (geosite/geoip).
    /// </summary>
    public static string GeoDataFile(string kind)
    {
        return Path.Combine(RootDirectory(), "geo", $"{kind}.dat");
    }

    /// <summary>
    /// Path to the shared SQLite state database.
    /// </summary>
    public static string StateDbFile()
    {
        return Path.Combine(RootDirectory(), "state.db");
    }

    /// <summary>
    /// Path to the shared application config file.
    /// </summary>
    public static string AppConfigFile()
    {
        return Path.Combine(RootDirectory(), "config.json");
    }

    private static string RootDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "AmneziaGeo");
    }
}
