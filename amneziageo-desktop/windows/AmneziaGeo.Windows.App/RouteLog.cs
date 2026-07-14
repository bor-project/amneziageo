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

    // Roll the file past this size, keeping RetainedBackups numbered generations (routes.log.1..N).
    private const long MaxBytes = 8_000_000;

    // Rotated generations kept alongside the live routes.log (routes.log.1 = newest backup).
    private const int RetainedBackups = 5;

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
        Append($"{level} {action,-14} {target} via {via}{(string.IsNullOrEmpty(note) ? string.Empty : "  " + note)}");
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

        Append($"[INF] {message}");
    }

    private static void Append(string body)
    {
        try
        {
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [pid {Environment.ProcessId}] {body}";
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

    // Caller holds Gate. Past MaxBytes, rotates routes.log -> .1, shifting older backups up (.k -> .k+1) and
    // dropping the oldest, giving the routing log the same bounded footprint the agent Serilog log already has.
    private static void Roll()
    {
        try
        {
            var info = new FileInfo(FilePath);
            if (!info.Exists || info.Length <= MaxBytes)
            {
                return;
            }

            var oldest = $"{FilePath}.{RetainedBackups}";
            if (File.Exists(oldest))
            {
                File.Delete(oldest);
            }

            for (var k = RetainedBackups - 1; k >= 1; k--)
            {
                var from = $"{FilePath}.{k}";
                if (File.Exists(from))
                {
                    File.Move(from, $"{FilePath}.{k + 1}");
                }
            }

            File.Move(FilePath, $"{FilePath}.1");
        }
        catch
        {
            // Roll failure is non-fatal.
        }
    }
}
