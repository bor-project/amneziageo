using System.Globalization;
using System.Text;

using AmneziaGeo.Dal;

namespace AmneziaGeo.Android.Ui.Services;

/// <summary>
/// Android agent log: writes leveled rows to the shared <see cref="SqliteLogStore"/> for the diagnostics
/// viewer and mirrors them to logcat. The capture floor gates what is stored; the routing log records
/// resolved routes when on.
/// </summary>
internal sealed class AndroidAgentLog : IDisposable
{
    private const string Tag = "AmneziaGeo";

    private readonly SqliteLogStore _store;
    private volatile int _captureFloor = 5;
    private volatile bool _routeLog;

    /// <summary>
    /// ctor
    /// </summary>
    public AndroidAgentLog(string databasePath)
    {
        _store = new SqliteLogStore(databasePath);
    }

    /// <summary>
    /// The log database the diagnostics archive reads.
    /// </summary>
    public SqliteLogStore Store => _store;

    /// <summary>
    /// Creates the log tables and starts the writer loop.
    /// </summary>
    public Task InitializeAsync(CancellationToken ct = default) => _store.InitializeAsync(ct);

    /// <summary>
    /// Sets the capture floor from a verbosity token; none stops capture.
    /// </summary>
    public void SetCaptureLevel(string token)
    {
        _captureFloor = FloorFor(token);
    }

    /// <summary>
    /// Turns the routing log on or off.
    /// </summary>
    public void SetRouteLog(bool on)
    {
        _routeLog = on;
    }

    /// <summary>
    /// Logs a debug row.
    /// </summary>
    public void Debug(string source, string message) => Agent(2, source, message);

    /// <summary>
    /// Logs an info row.
    /// </summary>
    public void Info(string source, string message) => Agent(3, source, message);

    /// <summary>
    /// Logs a warning row.
    /// </summary>
    public void Warn(string source, string message) => Agent(4, source, message);

    /// <summary>
    /// Logs an error row, with the exception when one is given.
    /// </summary>
    public void Error(string source, string message, Exception? error = null)
    {
        Agent(5, source, error is null ? message : $"{message}{Environment.NewLine}{error}");
    }

    /// <summary>
    /// Stores one row whatever the capture floor is: a switchover is rare and is read before anything else.
    /// </summary>
    public void Note(string source, string message)
    {
        Mirror(AmneziaGeo.Ipc.SwitchLog.LevelId, source, message);
        _store.AppendAgent(UnixMs(), AmneziaGeo.Ipc.SwitchLog.LevelId, source, message);
    }

    /// <summary>
    /// Stores one leveled agent row when the level clears the capture floor; always mirrors to logcat.
    /// </summary>
    public void Agent(int levelId, string source, string message)
    {
        Mirror(levelId, source, message);
        if (levelId >= _captureFloor)
        {
            _store.AppendAgent(UnixMs(), levelId, source, message);
        }
    }

    /// <summary>
    /// Stores one routing-log row when the routing log is on.
    /// </summary>
    public void Route(string message)
    {
        if (_routeLog)
        {
            _store.AppendRoute(UnixMs(), message);
        }
    }

    /// <summary>
    /// Reads a window of one log table newest-first.
    /// </summary>
    public Task<LogPage> QueryAsync(string table, long? beforeId, int limit, int? minLevelId, string? search, CancellationToken ct = default) =>
        _store.QueryAsync(table, beforeId, limit, minLevelId, search, ct);

    /// <summary>
    /// Counts rows matching the level/search filter.
    /// </summary>
    public Task<int> CountAsync(string table, int? minLevelId, string? search, CancellationToken ct = default) =>
        _store.CountAsync(table, minLevelId, search, ct);

    /// <summary>
    /// Empties one log table.
    /// </summary>
    public Task ClearAsync(string table, CancellationToken ct = default) => _store.ClearAsync(table, ct);

    /// <summary>
    /// Renders a whole log table to text for export.
    /// </summary>
    public Task<string> RenderAllAsync(string table, CancellationToken ct = default) => _store.RenderAsync(table, Render, ct);

    /// <summary>
    /// Renders one row: "yyyy-MM-dd HH:mm:ss.fff [LVL] source message"; routes carry no level or source.
    /// </summary>
    public static string Render(LogRow row)
    {
        var ts = DateTimeOffset.FromUnixTimeMilliseconds(row.UnixMs).LocalDateTime
            .ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        var sb = new StringBuilder(ts);
        if (row.Level is not null)
        {
            sb.Append(" [").Append(row.Level).Append(']');
        }

        if (!string.IsNullOrEmpty(row.Source))
        {
            sb.Append(' ').Append(row.Source);
        }

        sb.Append(' ').Append(row.Message);
        return sb.ToString();
    }

    /// <summary>
    /// Maps a viewer "show this level and up" token to a floor id; trace/none/unknown means no floor.
    /// </summary>
    public static int? MinId(string? token)
    {
        return token?.Trim().ToLowerInvariant() switch
        {
            "debug" => 2,
            "info" => 3,
            "warning" => 4,
            "error" => 5,
            _ => null,
        };
    }

    // Maps a capture token to a severity floor; none disables capture.
    private static int FloorFor(string? token)
    {
        return token?.Trim().ToLowerInvariant() switch
        {
            "none" => int.MaxValue,
            "error" => 5,
            "warning" => 4,
            "info" => 3,
            "debug" => 2,
            "trace" => 1,
            _ => 3,
        };
    }

    private static long UnixMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static void Mirror(int levelId, string source, string message)
    {
        var line = string.IsNullOrEmpty(source) ? message : source + " " + message;
        switch (levelId)
        {
            case >= 5:
                global::Android.Util.Log.Error(Tag, line);
                break;
            case 4:
                global::Android.Util.Log.Warn(Tag, line);
                break;
            case 3:
                global::Android.Util.Log.Info(Tag, line);
                break;
            case 2:
                global::Android.Util.Log.Debug(Tag, line);
                break;
            default:
                global::Android.Util.Log.Verbose(Tag, line);
                break;
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _store.Dispose();
}
