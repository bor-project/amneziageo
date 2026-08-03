using System.Globalization;
using System.Text;
using AmneziaGeo.Dal;

namespace AmneziaGeo.Linux.App;

/// <summary>
/// Agent log: writes leveled rows to the shared log database and mirrors them to the console.
/// </summary>
internal sealed class AgentLog : IDisposable
{
    private readonly SqliteLogStore _store;
    private volatile int _captureFloor = 5;
    private volatile bool _routeLog;

    /// <summary>
    /// ctor
    /// </summary>
    public AgentLog(string databasePath)
    {
        _store = new SqliteLogStore(databasePath);
    }

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
    /// Stores one leveled agent row when the level clears the capture floor; always mirrors to the console.
    /// </summary>
    public void Agent(int levelId, string source, string message)
    {
        Console.WriteLine($"{Stamp()} [{LevelToken(levelId)}] {source} {message}");
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
    /// Flushes pending rows to the database.
    /// </summary>
    public Task FlushAsync(CancellationToken ct = default) => _store.FlushAsync(ct);

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

    private static string LevelToken(int levelId)
    {
        return levelId switch
        {
            >= 5 => "ERR",
            4 => "WRN",
            3 => "INF",
            2 => "DBG",
            _ => "TRC",
        };
    }

    private static string Stamp() => DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

    private static long UnixMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <inheritdoc/>
    public void Dispose() => _store.Dispose();
}
