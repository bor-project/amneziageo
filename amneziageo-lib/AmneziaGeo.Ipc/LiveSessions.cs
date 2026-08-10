using System.Globalization;
using System.Text;

namespace AmneziaGeo.Ipc;

/// <summary>
/// What the relay decided for one destination and what it has carried there. The rate is what arrived since the
/// previous snapshot, not an average over the session: a destination that stopped moving has to read as stopped.
/// </summary>
public sealed record LiveSession(
    string Host,
    string Verdict,
    long Bytes = 0,
    long BitsPerSecond = -1,
    int Live = 0,
    int AgeSeconds = -1,
    int IdleSeconds = -1,
    string App = "")
{
    /// <summary>
    /// Seconds a held connection may carry nothing before it counts as stalled.
    /// </summary>
    public const int StallSeconds = 20;

    /// <summary>
    /// Whether something is connected there and nothing has moved for the stall window.
    /// </summary>
    public bool Stalled => Live > 0 && IdleSeconds >= StallSeconds;

    /// <summary>
    /// Renders the destination as one protocol row.
    /// </summary>
    public string ToRow()
    {
        var row = new StringBuilder("session\t").Append(Host).Append('\t').Append(Verdict);
        Pair(row, "bytes", Bytes);
        Pair(row, "bps", BitsPerSecond);
        Pair(row, "live", Live);
        Pair(row, "age", AgeSeconds);
        Pair(row, "idle", IdleSeconds);
        if (App.Length > 0)
        {
            row.Append("\tapp=").Append(App);
        }

        return row.ToString();
    }

    /// <summary>
    /// Reads a destination back from its protocol row.
    /// </summary>
    public static LiveSession? TryParse(string row)
    {
        var parts = row.Split('\t');
        if (parts.Length < 3 || parts[0] != "session")
        {
            return null;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in parts.Skip(3))
        {
            var split = field.IndexOf('=');
            if (split > 0)
            {
                values[field[..split]] = field[(split + 1)..];
            }
        }

        return new LiveSession(
            parts[1],
            parts[2],
            Math.Max(Number(values, "bytes"), 0),
            Number(values, "bps"),
            (int)Math.Max(Number(values, "live"), 0),
            (int)Number(values, "age"),
            (int)Number(values, "idle"),
            values.GetValueOrDefault("app", string.Empty));
    }

    /// <summary>
    /// Renders what the destination holds, in English, for the log and the support archive.
    /// </summary>
    public string Describe()
    {
        var parts = new List<string> { Verdict };
        if (App.Length > 0)
        {
            parts.Add(App);
        }

        parts.Add(CheckFormat.Bytes(Bytes));
        if (BitsPerSecond >= 0)
        {
            parts.Add($"{CheckFormat.Mbits(BitsPerSecond)} Mbit/s");
        }

        if (Live > 0)
        {
            parts.Add($"{Live.ToString(CultureInfo.InvariantCulture)} live");
        }

        if (AgeSeconds >= 0)
        {
            parts.Add($"held {AgeSeconds.ToString(CultureInfo.InvariantCulture)} s");
        }

        if (IdleSeconds >= 0)
        {
            parts.Add($"idle {IdleSeconds.ToString(CultureInfo.InvariantCulture)} s");
        }

        if (Stalled)
        {
            parts.Add("stalled");
        }

        return string.Join(", ", parts);
    }

    private static void Pair(StringBuilder row, string key, long value)
    {
        if (value >= 0)
        {
            row.Append('\t').Append(key).Append('=').Append(value.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static long Number(Dictionary<string, string> values, string key)
    {
        return values.TryGetValue(key, out var text)
            && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : -1;
    }
}

/// <summary>
/// What the relay holds right now. The tunnel and the head run in separate processes and share nothing but the
/// application's own files, so this is written whole by one and read whole by the other, and a snapshot older
/// than its window is no longer an answer about anything.
/// </summary>
public sealed record SessionReport(
    long UnixMs,
    IReadOnlyList<LiveSession> Sessions,
    int Held = 0,
    int Undecided = 0,
    long TotalBytes = 0)
{
    /// <summary>
    /// A report holding nothing, which is what a tunnel without a relay has to say.
    /// </summary>
    public static SessionReport Empty { get; } = new(0, []);

    /// <summary>
    /// The destination carrying the most traffic: the one a check has to compare the tunnel against, because it
    /// is the one the user is actually watching.
    /// </summary>
    public LiveSession? Busiest => Sessions.Count == 0 ? null : Sessions.MaxBy(one => one.Bytes);

    /// <summary>
    /// Destinations something is connected to that have carried nothing for the stall window.
    /// </summary>
    public int Stalled => Sessions.Count(one => one.Stalled);

    /// <summary>
    /// Renders the report as the ack payload: one row per destination, then the totals.
    /// </summary>
    public string ToPayload()
    {
        var rows = new List<string>();
        foreach (var session in Sessions)
        {
            rows.Add(session.ToRow());
        }

        rows.Add(string.Join(
            '\t',
            "held",
            Held.ToString(CultureInfo.InvariantCulture),
            Undecided.ToString(CultureInfo.InvariantCulture),
            TotalBytes.ToString(CultureInfo.InvariantCulture),
            UnixMs.ToString(CultureInfo.InvariantCulture)));

        return string.Join('\n', rows);
    }

    /// <summary>
    /// Reads a report back from its payload.
    /// </summary>
    public static SessionReport Parse(string payload)
    {
        var sessions = new List<LiveSession>();
        var held = 0;
        var undecided = 0;
        var total = 0L;
        var stamp = 0L;
        foreach (var row in payload.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (LiveSession.TryParse(row) is { } session)
            {
                sessions.Add(session);
                continue;
            }

            var parts = row.Split('\t');
            if (parts.Length >= 5 && parts[0] == "held")
            {
                held = (int)Number(parts[1]);
                undecided = (int)Number(parts[2]);
                total = Number(parts[3]);
                stamp = Number(parts[4]);
            }
        }

        return new SessionReport(stamp, sessions, held, undecided, total);
    }

    /// <summary>
    /// Renders the report in English for the agent log and the support archive.
    /// </summary>
    public string Render()
    {
        var text = new StringBuilder();
        text.Append("relay holds ").Append(Held.ToString(CultureInfo.InvariantCulture)).Append(" destination(s), ")
            .Append(Undecided.ToString(CultureInfo.InvariantCulture)).Append(" undecided, ")
            .Append(Stalled.ToString(CultureInfo.InvariantCulture)).Append(" stalled, ")
            .Append(CheckFormat.Bytes(TotalBytes)).Append(" carried\n");
        foreach (var session in Sessions)
        {
            text.Append("  ").Append(session.Host.PadRight(32)).Append(session.Describe()).Append('\n');
        }

        return text.ToString();
    }

    private static long Number(string text)
    {
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }
}
