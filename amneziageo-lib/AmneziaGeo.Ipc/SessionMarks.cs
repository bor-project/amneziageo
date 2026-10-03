using System.Globalization;

namespace AmneziaGeo.Ipc;

/// <summary>
/// What a session went through since it came up: when it was raised, how often it was raised again and how often
/// the link under it was repaired.
/// </summary>
public sealed class SessionMarks
{
    private const int KeyWidth = 17;

    private readonly Lock _gate = new();
    private string _name = string.Empty;
    private long _upSinceMs;
    private long _raisedAtMs;
    private long _downSinceMs;
    private int _raisedAgain;
    private int _rebinds;
    private int _resolves;
    private int _carriers;

    /// <summary>
    /// Notes that the tunnel of a configuration came up: the first time opens the session, every next one counts
    /// as the session raised again.
    /// </summary>
    public void Raised(long unixMs, string name)
    {
        lock (_gate)
        {
            if (_upSinceMs == 0 || !string.Equals(_name, name, StringComparison.Ordinal))
            {
                Reset();
                _name = name;
                _upSinceMs = unixMs;
            }
            else
            {
                _raisedAgain++;
            }

            _raisedAtMs = unixMs;
            _downSinceMs = 0;
        }
    }

    /// <summary>
    /// Notes that the tunnel went down to be raised again.
    /// </summary>
    public void Dropped(long unixMs)
    {
        lock (_gate)
        {
            if (_upSinceMs != 0 && _downSinceMs == 0)
            {
                _downSinceMs = unixMs;
            }
        }
    }

    /// <summary>
    /// Notes a repair that kept the tunnel standing.
    /// </summary>
    public void Repaired(RecoveryStep step)
    {
        lock (_gate)
        {
            switch (step)
            {
                case RecoveryStep.Rebind:
                    _rebinds++;
                    break;
                case RecoveryStep.Resolve:
                    _resolves++;
                    break;
                case RecoveryStep.Carrier:
                    _carriers++;
                    break;
                default:
                    break;
            }
        }
    }

    /// <summary>
    /// Forgets the session once its tunnel is taken down for good.
    /// </summary>
    public void Closed()
    {
        lock (_gate)
        {
            Reset();
        }
    }

    /// <summary>
    /// The session section of the support archive, line by line; the report adds how many of the destinations the
    /// session met carry a name.
    /// </summary>
    public IReadOnlyList<string> Lines(long nowUnixMs, SessionReport? held = null)
    {
        var lines = new List<string> { "[session]" };
        lock (_gate)
        {
            if (_upSinceMs == 0)
            {
                lines.Add(Row("up since", "- (no session is up)"));
            }
            else
            {
                lines.Add(Row("up since", $"{Moment(_upSinceMs)} ({Span(nowUnixMs - _upSinceMs)} ago)"));
                if (_downSinceMs != 0)
                {
                    lines.Add(Row("down since", Moment(_downSinceMs)));
                }

                lines.Add(Row("raised again", _raisedAgain == 0 ? "0" : $"{Number(_raisedAgain)} (last at {Moment(_raisedAtMs)})"));
                lines.Add(Row("link repairs", Repairs()));
            }
        }

        if (held is not null)
        {
            lines.Add(Row("names in cache", $"{Number(held.Named)} of {Number(held.Met)} address(es) the session met"));
        }

        return lines;
    }

    /// <summary>
    /// Renders the marks as one line another process reads back.
    /// </summary>
    public string ToPayload()
    {
        lock (_gate)
        {
            return string.Join(
                '\t',
                Number(_upSinceMs),
                Number(_raisedAtMs),
                Number(_downSinceMs),
                Number(_raisedAgain),
                Number(_rebinds),
                Number(_resolves),
                Number(_carriers),
                _name.Replace('\t', ' ').Replace('\n', ' '));
        }
    }

    /// <summary>
    /// Reads the marks back from their line; anything else gives the marks of no session.
    /// </summary>
    public static SessionMarks Parse(string? payload)
    {
        var marks = new SessionMarks();
        var parts = (payload ?? string.Empty).Trim('\r', '\n').Split('\t');
        var numbers = new long[7];
        if (parts.Length < 8)
        {
            return marks;
        }

        for (var i = 0; i < numbers.Length; i++)
        {
            if (!long.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out numbers[i]) || numbers[i] < 0)
            {
                return marks;
            }
        }

        marks._upSinceMs = numbers[0];
        marks._raisedAtMs = numbers[1];
        marks._downSinceMs = numbers[2];
        marks._raisedAgain = (int)numbers[3];
        marks._rebinds = (int)numbers[4];
        marks._resolves = (int)numbers[5];
        marks._carriers = (int)numbers[6];
        marks._name = parts[7];
        return marks;
    }

    private void Reset()
    {
        _name = string.Empty;
        _upSinceMs = 0;
        _raisedAtMs = 0;
        _downSinceMs = 0;
        _raisedAgain = 0;
        _rebinds = 0;
        _resolves = 0;
        _carriers = 0;
    }

    // The repairs by what each of them changed.
    private string Repairs()
    {
        var total = _rebinds + _resolves + _carriers;
        if (total == 0)
        {
            return "0";
        }

        var parts = new List<string>();
        if (_rebinds > 0)
        {
            parts.Add($"another source port {Number(_rebinds)}");
        }

        if (_resolves > 0)
        {
            parts.Add($"the address of the server resolved again {Number(_resolves)}");
        }

        if (_carriers > 0)
        {
            parts.Add($"the carrier dialled again {Number(_carriers)}");
        }

        return $"{Number(total)} ({string.Join(", ", parts)})";
    }

    private static string Row(string key, string value)
    {
        return (key + ":").PadRight(KeyWidth) + value;
    }

    private static string Moment(long unixMs)
    {
        return DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime()
            .ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
    }

    private static string Span(long ms)
    {
        var seconds = Math.Max(ms, 0) / 1000;
        if (seconds < 120)
        {
            return $"{Number(seconds)} s";
        }

        var minutes = seconds / 60;
        return minutes < 120 ? $"{Number(minutes)} min" : $"{Number(minutes / 60)} h {Number(minutes % 60)} min";
    }

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
