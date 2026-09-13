namespace AmneziaGeo.Dal;

/// <summary>
/// Agent-log writer for the client processes (tray, GUI): rows go to the agent over the link the process holds, and
/// the ones it never takes land in the per-user log database (#209). Best effort - a logging fault never surfaces to the caller.
/// </summary>
public static class ClientLog
{
    /// <summary>
    /// The source of the tray's rows.
    /// </summary>
    public const string TraySource = "Tray";

    /// <summary>
    /// The source of the GUI's rows.
    /// </summary>
    public const string UiSource = "Ui";

    private const int LevelInfo = 3;
    private const int LevelWarning = 4;
    private const int LevelError = 5;

    private static ClientLogQueue? _queue;
    private static string _source = "client";

    /// <summary>
    /// Binds the source written with every row and the per-user log database for the rows the agent does not take.
    /// </summary>
    public static void Open(string databasePath, string source)
    {
        _source = source;
        _queue = new ClientLogQueue(databasePath);
    }

    /// <summary>
    /// Whether rows under this source come from a client process.
    /// </summary>
    public static bool IsSource(string source)
    {
        return source is TraySource or UiSource;
    }

    /// <summary>
    /// Hands the rows to the agent through a link.
    /// </summary>
    public static void Attach(Func<ClientLogRow, Task<bool>> relay)
    {
        _queue?.Attach(relay);
    }

    /// <summary>
    /// Keeps the rows queued until the link is back.
    /// </summary>
    public static void Detach()
    {
        _queue?.Detach();
    }

    /// <summary>
    /// Records an informational event.
    /// </summary>
    public static void Info(string message)
    {
        Append(LevelInfo, message);
    }

    /// <summary>
    /// Records a warning.
    /// </summary>
    public static void Warning(string message)
    {
        Append(LevelWarning, message);
    }

    /// <summary>
    /// Records an error, appending the exception when one is given.
    /// </summary>
    public static void Error(string message, Exception? error = null)
    {
        Append(LevelError, error is null ? message : message + Environment.NewLine + error);
    }

    /// <summary>
    /// Waits until the queued rows are with the agent or on disk; called before a process exits.
    /// </summary>
    public static void Flush(int timeoutMs = 2000)
    {
        _queue?.Flush(timeoutMs);
    }

    private static void Append(int levelId, string message)
    {
        try
        {
            _queue?.Append(new ClientLogRow(DateTimeOffset.Now.ToUnixTimeMilliseconds(), levelId, _source, message));
        }
        catch (Exception)
        {
            // Callers include a native window procedure, which must never see an exception from logging.
        }
    }
}
