namespace AmneziaGeo.Windows.App;

/// <summary>
/// A dedicated, separately toggleable routing log (#82 follow-up): every route-table change and every
/// matched DNS resolution is appended, one line per action, to <c>routes.log</c> in the log directory when
/// enabled. It is OFF by default and independent of the main log's verbosity, so a support engineer can ask
/// the user to "включить лог маршрутизации", reproduce a "why isn't X routed / why is it slow" problem, and
/// read exactly which /32 routes were installed for which domain and whether they succeeded - without the
/// noise of a full Trace log. Enabled live in both processes (agent and per-tunnel service) via the
/// "route-log" setting, applied on the same poll as the log level (<see cref="LogLevelWatcher"/>).
///
/// When disabled, <see cref="Write"/> returns on a single volatile read, so the instrumentation sprinkled
/// through <see cref="RouteManager"/> and <see cref="DomainTracker"/> costs nothing on the hot resolve path.
/// The file is included in the diagnostics bundle. Writes open/append/close each line with a shared handle so
/// the two processes can both append safely and nothing keeps the file locked against the bundle collector.
/// </summary>
internal static class RouteLog
{
    /// <summary>The settings key that turns the routing log on/off (persisted as a bool).</summary>
    public const string SettingKey = "route-log";

    // Roll the file once it passes this size so a routing log left on does not grow without bound; the
    // previous generation is kept as routes.log.1 (a single backup) and overwritten on the next roll.
    private const long MaxBytes = 8_000_000;

    private static readonly object Gate = new();
    private static volatile bool _enabled;

    /// <summary>Whether route actions are currently being recorded. Set by the settings poll in both processes.</summary>
    public static bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    private static string FilePath => Path.Combine(TunnelPaths.LogDirectory(), "routes.log");

    /// <summary>
    /// Records a route-table action: an add/remove of a route, tagged with what it targets, the next hop or
    /// interface it goes via, and whether the OS call succeeded. No-op when the routing log is disabled.
    /// </summary>
    public static void Write(string action, string target, string via, bool ok, string? note = null)
    {
        if (!_enabled)
        {
            return;
        }

        var status = ok ? "OK  " : "FAIL";
        Append($"{status} {action,-14} {target} via {via}{(string.IsNullOrEmpty(note) ? string.Empty : "  " + note)}");
    }

    /// <summary>
    /// Records a free-form routing event (e.g. a matched DNS resolution "domain -> ips") so the log reads as
    /// a story: resolve, then the routes installed for it. No-op when the routing log is disabled.
    /// </summary>
    public static void Note(string message)
    {
        if (!_enabled)
        {
            return;
        }

        Append(message);
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
            // A diagnostic log must never take down routing: swallow any IO failure (disk full, locked file).
        }
    }

    // Caller holds Gate. Rolls routes.log to routes.log.1 once it passes MaxBytes, best-effort.
    private static void Roll()
    {
        try
        {
            var info = new FileInfo(FilePath);
            if (info.Exists && info.Length > MaxBytes)
            {
                var backup = FilePath + ".1";
                if (File.Exists(backup))
                {
                    File.Delete(backup);
                }

                File.Move(FilePath, backup);
            }
        }
        catch
        {
            // If the roll fails the file just keeps growing until the next successful roll; not worth failing on.
        }
    }
}
