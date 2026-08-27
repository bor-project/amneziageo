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
    /// Settings key carrying the last connect-failure message for a tunnel.
    /// </summary>
    public static string ConnectMessageKey(string name)
    {
        return $"connect-error:{name}";
    }

    /// <summary>
    /// Settings key carrying the structured connect-failure reason token for a tunnel.
    /// </summary>
    public static string ConnectReasonKey(string name)
    {
        return $"connect-reason:{name}";
    }

    /// <summary>
    /// Settings key carrying the last-known-good resolved endpoint IP for a tunnel.
    /// </summary>
    public static string EndpointIpKey(string name)
    {
        return $"endpoint-ip:{name}";
    }

    /// <summary>
    /// Directory holding the stored wg-quick configs.
    /// </summary>
    public static string ConfigurationsDirectory()
    {
        return Path.Combine(RootDirectory(), "config");
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
        return Path.Combine(MachineRoot(), "geo", $"{kind}.dat");
    }

    /// <summary>
    /// Path to the bundled wstunnel client executable.
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
    /// Path to a user's SQLite state database under a resolved data root.
    /// </summary>
    public static string StateDbFile(string root)
    {
        return Path.Combine(root, "state.db");
    }

    /// <summary>
    /// Path to the machine-wide state database (shared geo assets and machine settings).
    /// </summary>
    public static string MachineDbFile()
    {
        return Path.Combine(AppDataRoot.MachineBase(), "machine.db");
    }

    /// <summary>
    /// Path to a tunnel's persisted DNS-redirect state used to revert NIC DNS after a stop.
    /// </summary>
    public static string DnsStateFile(string name)
    {
        return Path.Combine(MachineRoot(), $"dns-state-{Sanitize(name)}.txt");
    }

    /// <summary>
    /// All persisted DNS-redirect state files (any tunnel), so a reconciler can revert leftovers.
    /// </summary>
    public static IEnumerable<string> DnsStateFiles()
    {
        return EnumerateState("dns-state*.txt");
    }

    /// <summary>
    /// Path to a tunnel's persisted endpoint-exclusion routes.
    /// </summary>
    public static string RouteStateFile(string name)
    {
        return Path.Combine(MachineRoot(), $"route-state-{Sanitize(name)}.txt");
    }

    /// <summary>
    /// All persisted endpoint-exclusion state files (any tunnel), including a pre-rename global one.
    /// </summary>
    public static IEnumerable<string> RouteStateFiles()
    {
        return EnumerateState("route-state*.txt");
    }

    /// <summary>
    /// Path to a tunnel's persisted LAN-bypass exclusion routes.
    /// </summary>
    public static string LanStateFile(string name)
    {
        return Path.Combine(MachineRoot(), $"lan-state-{Sanitize(name)}.txt");
    }

    /// <summary>
    /// All persisted LAN-bypass exclusion state files (any tunnel), so a reconciler can revert leftovers.
    /// </summary>
    public static IEnumerable<string> LanStateFiles()
    {
        return EnumerateState("lan-state*.txt");
    }

    /// <summary>
    /// The state files the named tunnels own, so a sweep leaves a live tunnel's record in place.
    /// </summary>
    public static IReadOnlySet<string> Owned(IEnumerable<string>? names, Func<string, string> file)
    {
        var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names ?? [])
        {
            owned.Add(file(name));
        }

        return owned;
    }

    /// <summary>
    /// Directory holding service log files.
    /// </summary>
    public static string LogDirectory()
    {
        return Path.Combine(MachineRoot(), "logs");
    }

    /// <summary>
    /// Path to the agent service log file.
    /// </summary>
    public static string AgentLogFile()
    {
        return Path.Combine(LogDirectory(), "agent.log");
    }

    /// <summary>
    /// Path to the structured log database (ageo + routes tables).
    /// </summary>
    public static string LogDbFile()
    {
        return Path.Combine(LogDirectory(), "log.db");
    }

    /// <summary>
    /// Path to the hidden logging settings file (retention cap).
    /// </summary>
    public static string LogSettingsFile()
    {
        return Path.Combine(LogDirectory(), "settings.json");
    }

    /// <summary>
    /// Directory where collected diagnostics bundles are written.
    /// </summary>
    public static string DiagnosticsDirectory()
    {
        return Path.Combine(MachineRoot(), "diagnostics");
    }

    private static IEnumerable<string> EnumerateState(string pattern)
    {
        var dir = MachineRoot();
        return Directory.Exists(dir) ? Directory.EnumerateFiles(dir, pattern) : [];
    }

    private static string Sanitize(string name)
    {
        return string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
    }

    private static string RootDirectory()
    {
        return AppDataRoot.Base();
    }

    private static string MachineRoot()
    {
        return AppDataRoot.MachineBase();
    }
}
