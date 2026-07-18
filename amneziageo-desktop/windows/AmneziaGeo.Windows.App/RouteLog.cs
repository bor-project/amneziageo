namespace AmneziaGeo.Windows.App;

/// <summary>
/// Toggleable routing log: route-table changes, matched DNS resolutions, outbound destinations.
/// </summary>
internal static class RouteLog
{
    /// <summary>
    /// Settings key that toggles the routing log.
    /// </summary>
    public const string SettingKey = "route-log";

    private static readonly object Gate = new();
    private static volatile bool _enabled;

    /// <summary>
    /// Whether route actions are currently being recorded.
    /// </summary>
    public static bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    private static string FilePath => Path.Combine(TunnelPaths.LogDirectory(), "routes.log");

    /// <summary>
    /// Records a route-table add/remove with its target and next hop.
    /// </summary>
    public static void Write(string action, string target, string via, bool ok, string? note = null)
    {
        if (!_enabled)
        {
            return;
        }

        var level = ok ? "[INF]" : "[ERR]";
        Append(level, $"{action,-14} {target} via {via}{(string.IsNullOrEmpty(note) ? string.Empty : "  " + note)}");
    }

    /// <summary>
    /// Records a free-form routing event.
    /// </summary>
    public static void Note(string message)
    {
        if (!_enabled)
        {
            return;
        }

        Append("[INF]", message);
    }

    // Shared line shape with the agent log: timestamp, level, source column ("route"), message.
    private static void Append(string level, string body)
    {
        try
        {
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {level} route {body}";
            lock (Gate)
            {
                Directory.CreateDirectory(TunnelPaths.LogDirectory());
                Roll();
                using var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(stream);
                writer.WriteLine(line);
            }
        }
        catch
        {
            // Swallow IO failures; a log must not break routing.
        }
    }

    // Caller holds Gate. Rotates once past the shared roll threshold, keeping the same bounded footprint as
    // the agent log (LogRoller).
    private static void Roll()
    {
        try
        {
            var info = new FileInfo(FilePath);
            if (!info.Exists || info.Length <= LogRoller.MaxBytes)
            {
                return;
            }

            LogRoller.Roll(FilePath);
        }
        catch
        {
            // Roll failure is non-fatal.
        }
    }
}
