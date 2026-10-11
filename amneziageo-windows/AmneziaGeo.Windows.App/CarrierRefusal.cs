using System.Globalization;
using System.Text.RegularExpressions;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// What a line of a carrier process is, read for the refusal of its front.
/// </summary>
internal enum CarrierLine
{
    /// <summary>
    /// A line written before any refusal.
    /// </summary>
    Plain,

    /// <summary>
    /// The line that says the front refused the websocket.
    /// </summary>
    Refused,

    /// <summary>
    /// A line written after the refusal.
    /// </summary>
    After,
}

/// <summary>
/// Reads the lines of one carrier process for the refusal of its front: wstunnel asks a front that answered its
/// upgrade with anything but the switch again at once and without end, so the process is stopped on the first one.
/// </summary>
internal sealed partial class CarrierRefusal
{
    // What wstunnel writes when the front answers its upgrade with something else than the switch.
    private const string Mark = "failed to do websocket handshake";
    private const string CauseHead = "Caused by:";
    private const string StatusHead = "Invalid status code:";
    private const int CauseMax = 120;

    private readonly Lock _gate = new();
    private bool _refused;
    private bool _closed;
    private string _cause = string.Empty;

    /// <summary>
    /// Whether the front refused the websocket of the process.
    /// </summary>
    public bool Refused
    {
        get
        {
            lock (_gate)
            {
                return _refused;
            }
        }
    }

    /// <summary>
    /// What the front answered, empty while the process has not said.
    /// </summary>
    public string Cause
    {
        get
        {
            lock (_gate)
            {
                return _cause;
            }
        }
    }

    /// <summary>
    /// Takes the next line of the process and says what it is.
    /// </summary>
    public CarrierLine Take(string line)
    {
        var text = Colours().Replace(line, string.Empty);
        lock (_gate)
        {
            if (_refused)
            {
                Read(text);
                return CarrierLine.After;
            }

            if (!text.Contains(Mark, StringComparison.OrdinalIgnoreCase))
            {
                return CarrierLine.Plain;
            }

            _refused = true;
            return CarrierLine.Refused;
        }
    }

    // Keeps the last line of the causes written under the refusal.
    private void Read(string text)
    {
        var trimmed = text.Trim();
        if (_closed || trimmed.Length == 0 || trimmed.Equals(CauseHead, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!char.IsWhiteSpace(text[0]))
        {
            _closed = true;
            return;
        }

        _cause = Name(Index().Replace(trimmed, string.Empty));
    }

    // Names a cause: the status of an answer as HTTP and its number, anything else in its own words.
    private static string Name(string cause)
    {
        if (cause.StartsWith(StatusHead, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(cause[StatusHead.Length..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var status))
        {
            return string.Create(CultureInfo.InvariantCulture, $"HTTP {status}");
        }

        return cause.Length > CauseMax ? cause[..CauseMax] : cause;
    }

    [GeneratedRegex(@"\x1B\[[0-9;]*[A-Za-z]")]
    private static partial Regex Colours();

    [GeneratedRegex(@"^\d+:\s*")]
    private static partial Regex Index();
}
