namespace AmneziaGeo.Ipc;

/// <summary>
/// Tells whether an app rule names this application itself. Such a rule sends the agent's own downloads, the
/// DNS proxy upstream and the websocket carrier into the tunnel they run.
/// </summary>
public static class OwnAppRule
{
    // Images that belong to this application wherever they stand.
    private static readonly string[] Images =
    [
        "amneziageo.exe",
        "amneziageo.windows.app.exe",
        "amneziageo.windows.tray.exe",
        "amneziageo.windows.ui.exe",
    ];

    /// <summary>
    /// Whether the token names this application: one of its images, its own folder, or one of its services.
    /// </summary>
    public static bool Names(string token)
    {
        var value = Value(token);
        if (value.Length == 0)
        {
            return false;
        }

        if (token.StartsWith("app:svc=", StringComparison.OrdinalIgnoreCase))
        {
            return value.StartsWith("AmneziaGeo", StringComparison.OrdinalIgnoreCase);
        }

        if (token.StartsWith("app:pkg=", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var path = AppPathToken.Tokenize(value.Replace('/', '\\').Trim('"').TrimEnd('\\'));
        var leaf = path[(path.LastIndexOf('\\') + 1)..];
        if (Images.Contains(leaf, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        // The install folder holds the helpers too (gateway, wstunnel), and a folder above it holds all of them.
        var own = OwnFolder();
        return own.Length > 0
            && (own.Equals(path, StringComparison.OrdinalIgnoreCase)
                || own.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(own + "\\", StringComparison.OrdinalIgnoreCase));
    }

    // Where this process runs from, in the same words a rule is written in.
    private static string OwnFolder() =>
        AppPathToken.Tokenize(AppContext.BaseDirectory.TrimEnd('\\', '/'));

    // What the token names, without its kind.
    private static string Value(string token)
    {
        var eq = token.IndexOf('=');
        return eq > 0 ? token[(eq + 1)..].Trim() : string.Empty;
    }
}
