namespace AmneziaGeo.Dal;

/// <summary>
/// Agent-log writer for the client processes (tray, GUI), which have no Serilog pipeline of their own: rows
/// land in the ageo table of the shared log database under the caller's source, so a failure outside the
/// service shows up in the same journal (#209). Best effort - a logging fault never surfaces to the caller.
/// </summary>
public static class ClientLog
{
    private const int LevelInfo = 3;
    private const int LevelWarning = 4;
    private const int LevelError = 5;

    private static SqliteLogStore? _store;
    private static string _source = "client";

    /// <summary>
    /// Opens the shared log database and binds the source written with every row.
    /// </summary>
    public static void Open(string databasePath, string source)
    {
        try
        {
            var directory = Path.GetDirectoryName(databasePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var store = new SqliteLogStore(databasePath);
            _source = source;
            _store = store;

            // Rows written before the schema is ready wait in the store's queue; its writer loop drains them.
            _ = InitializeAsync(store);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _store = null;
        }
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
    /// Waits until the queued rows are on disk; called before a process exits.
    /// </summary>
    public static void Flush(int timeoutMs = 2000)
    {
        var store = _store;
        if (store is null)
        {
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(timeoutMs);
            store.FlushAsync(timeout.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    private static async Task InitializeAsync(SqliteLogStore store)
    {
        try
        {
            await store.InitializeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // No writer loop, so drop the store instead of queueing rows nothing will drain.
            _store = null;
        }
    }

    private static void Append(int levelId, string message)
    {
        try
        {
            _store?.AppendAgent(DateTimeOffset.Now.ToUnixTimeMilliseconds(), levelId, _source, message);
        }
        catch (Exception)
        {
            // Callers include a native window procedure, which must never see an exception from logging.
        }
    }
}
