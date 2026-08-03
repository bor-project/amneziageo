namespace AmneziaGeo.Linux.App;

/// <summary>
/// Filesystem layout of the agent library.
/// </summary>
internal static class AgentPaths
{
    /// <summary>
    /// Root directory of the agent library.
    /// </summary>
    public static string Root { get; } = Resolve();

    /// <summary>
    /// State database path.
    /// </summary>
    public static string StateDb => Path.Combine(Root, "state.db");

    /// <summary>
    /// Log database path.
    /// </summary>
    public static string LogDb => Path.Combine(Root, "logs", "log.db");

    /// <summary>
    /// Directory holding the downloaded geo databases.
    /// </summary>
    public static string GeoDirectory => Path.Combine(Root, "geo");

    // AMNEZIAGEO_DATA overrides the root so a sudo-run agent can keep one library with the desktop session.
    private static string Resolve()
    {
        var custom = Environment.GetEnvironmentVariable("AMNEZIAGEO_DATA");
        var root = string.IsNullOrWhiteSpace(custom)
            ? Path.Combine(DataHome(), "AmneziaGeo")
            : custom.Trim();
        Directory.CreateDirectory(Path.Combine(root, "logs"));
        return root;
    }

    // Keeps one library for the desktop user: sudo moves the data home to root, a snapped editor moves it into its sandbox.
    private static string DataHome()
    {
        var elevated = Environment.GetEnvironmentVariable("SUDO_USER");
        if (!string.IsNullOrEmpty(elevated) && PasswdHome(elevated) is { } shared)
        {
            return Path.Combine(shared, ".local", "share");
        }

        // GetFolderPath returns an empty string while the XDG directory is absent, which a root run hits.
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create);
        if (local.Length > 0 && !local.Contains("/snap/", StringComparison.Ordinal))
        {
            return local;
        }

        var home = PasswdHome(Environment.UserName) ?? Environment.GetEnvironmentVariable("HOME");
        return string.IsNullOrEmpty(home) ? "/var/lib" : Path.Combine(home, ".local", "share");
    }

    // Reads the home directory a user is registered with.
    private static string? PasswdHome(string user)
    {
        try
        {
            foreach (var line in File.ReadLines("/etc/passwd"))
            {
                var fields = line.Split(':');
                if (fields.Length > 5 && fields[0] == user && fields[5].Length > 0)
                {
                    return fields[5];
                }
            }
        }
        catch (IOException)
        {
        }

        return null;
    }
}
