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
    /// Windows service name for the always-on agent.
    /// </summary>
    public static string AgentServiceName()
    {
        return "AmneziaGeoAgent";
    }

    /// <summary>
    /// Settings-store key carrying the last connect-failure reason for a tunnel. The per-tunnel service
    /// process writes it on a setup failure; the agent reads it back on a failed connect so the cause shows
    /// up in the UI journal (the per-tunnel process's own log file is not visible to the UI).
    /// </summary>
    public static string ConnectMessageKey(string name)
    {
        return $"connect-error:{name}";
    }

    /// <summary>
    /// Directory holding the stored wg-quick configs.
    /// </summary>
    public static string ConfigurationsDirectory()
    {
        return Path.Combine(RootDirectory(), "Configurations");
    }

    /// <summary>
    /// Path to the stored wg-quick config for a tunnel.
    /// </summary>
    public static string ConfigFile(string name)
    {
        return Path.Combine(ConfigurationsDirectory(), $"{name}.conf");
    }

    /// <summary>
    /// Path to a downloaded geo database file (geosite/geoip).
    /// </summary>
    public static string GeoDataFile(string kind)
    {
        return Path.Combine(RootDirectory(), "geo", $"{kind}.dat");
    }

    /// <summary>
    /// Path to the bundled wstunnel client executable, shipped next to the agent so the privileged
    /// service can launch it as the UDP-over-TCP transport. Lives in the install directory, not ProgramData.
    /// </summary>
    public static string WsTunnelExe()
    {
        return Path.Combine(AppContext.BaseDirectory, "wstunnel.exe");
    }

    /// <summary>
    /// Path to the shared SQLite state database.
    /// </summary>
    public static string StateDbFile()
    {
        return Path.Combine(RootDirectory(), "state.db");
    }

    /// <summary>
    /// Path to the bundled default-configuration database laid next to the agent by the installer when
    /// installer.config.json supplies a defaultConfigDb (#54). Consumed once by <see cref="SeedImporter"/>
    /// into <see cref="StateDbFile"/>. Lives in the install directory, not ProgramData.
    /// </summary>
    public static string SeedDbFile()
    {
        return Path.Combine(AppContext.BaseDirectory, "state.db.seed");
    }

    /// <summary>
    /// Marker laid next to the agent (by the installer) when the bundled default DB should overwrite an
    /// existing <see cref="StateDbFile"/> on conflict (the replace-on-conflict choice, #54). Its mere
    /// presence means "replace"; absence means "keep the existing database".
    /// </summary>
    public static string SeedReplaceFlagFile()
    {
        return Path.Combine(AppContext.BaseDirectory, "state.db.seed.replace");
    }

    /// <summary>
    /// Path to a tunnel's persisted DNS-redirect state used to revert NIC DNS after a stop.
    /// </summary>
    public static string DnsStateFile(string name)
    {
        return Path.Combine(RootDirectory(), $"dns-state-{Sanitize(name)}.txt");
    }

    /// <summary>
    /// All persisted DNS-redirect state files (any tunnel), so a reconciler can revert leftovers.
    /// </summary>
    public static IEnumerable<string> DnsStateFiles()
    {
        return EnumerateState("dns-state*.txt");
    }

    /// <summary>
    /// Path to a tunnel's persisted endpoint-exclusion routes used to revert them after a stop, even
    /// from another process when the tunnel exited without running its teardown.
    /// </summary>
    public static string RouteStateFile(string name)
    {
        return Path.Combine(RootDirectory(), $"route-state-{Sanitize(name)}.txt");
    }

    /// <summary>
    /// All persisted endpoint-exclusion state files (any tunnel), including a pre-rename global one.
    /// </summary>
    public static IEnumerable<string> RouteStateFiles()
    {
        return EnumerateState("route-state*.txt");
    }

    /// <summary>
    /// Path to a tunnel's persisted LAN-bypass exclusion routes (the RFC1918 ranges kept off the tunnel
    /// in full-tunnel mode), used to revert them after a stop, even from another process when the tunnel
    /// exited without running its teardown.
    /// </summary>
    public static string LanStateFile(string name)
    {
        return Path.Combine(RootDirectory(), $"lan-state-{Sanitize(name)}.txt");
    }

    /// <summary>
    /// All persisted LAN-bypass exclusion state files (any tunnel), so a reconciler can revert leftovers.
    /// </summary>
    public static IEnumerable<string> LanStateFiles()
    {
        return EnumerateState("lan-state*.txt");
    }

    /// <summary>
    /// Directory holding service log files.
    /// </summary>
    public static string LogDirectory()
    {
        return Path.Combine(RootDirectory(), "logs");
    }

    /// <summary>
    /// Path to the agent service log file.
    /// </summary>
    public static string AgentLogFile()
    {
        return Path.Combine(LogDirectory(), "agent.log");
    }

    /// <summary>
    /// Directory where collected diagnostics bundles are written (#82). Under ProgramData, so a bundle the
    /// SYSTEM agent writes is readable by the unprivileged UI, which copies it to the user's chosen location.
    /// </summary>
    public static string DiagnosticsDirectory()
    {
        return Path.Combine(RootDirectory(), "diagnostics");
    }

    private static IEnumerable<string> EnumerateState(string pattern)
    {
        var dir = RootDirectory();
        return Directory.Exists(dir) ? Directory.EnumerateFiles(dir, pattern) : [];
    }

    private static string Sanitize(string name)
    {
        return string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
    }

    private static string RootDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "AmneziaGeo");
    }
}
