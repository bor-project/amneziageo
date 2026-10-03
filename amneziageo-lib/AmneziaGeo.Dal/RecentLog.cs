using System.Text;
using AmneziaGeo.Decl;

namespace AmneziaGeo.Dal;

/// <summary>
/// Keeps the last journal rows of level info and above in memory, whatever the capture floor stores.
/// </summary>
public sealed class RecentLog
{
    /// <summary>
    /// Rows kept by default.
    /// </summary>
    public const int DefaultCapacity = 2000;

    /// <summary>
    /// Lowest level a row is kept from, in the dictionary ids of the log store.
    /// </summary>
    public const int FloorLevelId = 3;

    // Milliseconds within which a row told by two processes counts as one.
    private const long TwinMs = 5000;

    // Names of the levels by their dictionary ids.
    private static readonly string[] _levels = ["VRB", "VRB", "DBG", "INF", "WRN", "ERR", "FTL"];

    private readonly Lock _gate = new();
    private readonly LogRow[] _rows;
    private int _next;
    private int _count;
    private long _id;

    /// <summary>
    /// ctor
    /// </summary>
    public RecentLog(int capacity = DefaultCapacity)
    {
        _rows = new LogRow[Math.Max(capacity, 1)];
    }

    /// <summary>
    /// Keeps one row when its level is info or above.
    /// </summary>
    public void Add(long unixMs, int levelId, string? source, string message)
    {
        if (levelId < FloorLevelId)
        {
            return;
        }

        var row = new LogRow(0, unixMs, _levels[Math.Clamp(levelId, 0, _levels.Length - 1)], source, message);
        lock (_gate)
        {
            _rows[_next] = row with { Id = ++_id };
            _next = (_next + 1) % _rows.Length;
            _count = Math.Min(_count + 1, _rows.Length);
        }
    }

    /// <summary>
    /// The rows kept, oldest first.
    /// </summary>
    public IReadOnlyList<LogRow> Snapshot()
    {
        lock (_gate)
        {
            var rows = new List<LogRow>(_count);
            var first = (_next - _count + _rows.Length) % _rows.Length;
            for (var i = 0; i < _count; i++)
            {
                rows.Add(_rows[(first + i) % _rows.Length]);
            }

            return rows;
        }
    }

    /// <summary>
    /// Renders rows as lines another process reads back.
    /// </summary>
    public static string ToPayload(IReadOnlyList<LogRow> rows)
    {
        var text = new StringBuilder();
        foreach (var row in rows)
        {
            text.Append(LogLine.Compose(row.UnixMs, row.Level, row.Source, row.Message)).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>
    /// Reads rows back from their lines; a line that is not a row is skipped.
    /// </summary>
    public static IReadOnlyList<LogRow> Parse(string? payload)
    {
        var rows = new List<LogRow>();
        foreach (var line in (payload ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (LogLine.TryParse(line, out var unixMs, out var level, out var source, out var message))
            {
                rows.Add(new LogRow(rows.Count + 1, unixMs, level.Length == 0 ? null : level, source, message));
            }
        }

        return rows;
    }

    /// <summary>
    /// Joins the rows of two processes by time; a row both of them told is kept once, as the other one told it.
    /// </summary>
    public static IReadOnlyList<LogRow> Merge(IReadOnlyList<LogRow> own, IReadOnlyList<LogRow> other)
    {
        var told = new Dictionary<(string Source, string Message), List<long>>();
        foreach (var row in other)
        {
            var key = (row.Source ?? string.Empty, row.Message);
            if (!told.TryGetValue(key, out var times))
            {
                times = [];
                told[key] = times;
            }

            times.Add(row.UnixMs);
        }

        var rows = new List<LogRow>(own.Count + other.Count);
        foreach (var row in own)
        {
            if (told.TryGetValue((row.Source ?? string.Empty, row.Message), out var times))
            {
                var twin = times.FindIndex(time => Math.Abs(time - row.UnixMs) <= TwinMs);
                if (twin >= 0)
                {
                    times.RemoveAt(twin);
                    continue;
                }
            }

            rows.Add(row);
        }

        rows.AddRange(other);
        return [.. rows.OrderBy(row => row.UnixMs)];
    }
}
