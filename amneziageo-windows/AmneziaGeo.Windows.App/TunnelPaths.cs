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
        return Path.Combine(ConfigDirectory(), $"{name}.conf");
    }

    /// <summary>
    /// Path to the geo settings sidecar for a tunnel.
    /// </summary>
    public static string GeoFile(string name)
    {
        return Path.Combine(ConfigDirectory(), $"{name}.geo.json");
    }

    private static string ConfigDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "AmneziaGeo",
            "Configurations");
    }
}
