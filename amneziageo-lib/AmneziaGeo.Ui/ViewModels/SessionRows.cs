using System.Net;
using System.Text;

using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;

namespace AmneziaGeo.Ui.ViewModels;

/// <summary>
/// One destination the tunnel decides for: the address, the name it came with, where it goes, what settled it,
/// how long it is held and whatever else it carries.
/// </summary>
internal sealed record LiveRowItem(
    string Host,
    string Name,
    string Way,
    string WayText,
    string Why,
    string Left,
    string Detail,
    int Idle,
    bool Stalled)
{
    /// <summary>
    /// Whether the row carries anything beyond where it goes and why.
    /// </summary>
    public bool HasDetail => Detail.Length > 0;

    /// <summary>
    /// The row as one line, for the clipboard.
    /// </summary>
    public string Line
    {
        get
        {
            var parts = new List<string> { Host };
            if (Name.Length > 0)
            {
                parts.Add(Name);
            }

            parts.Add(WayText);
            parts.Add(Why);
            if (Left.Length > 0)
            {
                parts.Add(Left);
            }

            if (Detail.Length > 0)
            {
                parts.Add(Detail);
            }

            return string.Join("  ", parts);
        }
    }
}

/// <summary>
/// Renders what the tunnel decides for: the lines that count it, the rows behind it and the same rows as text.
/// </summary>
internal static class SessionRows
{
    // Widest a column is padded to; a longer value pushes the rest of its own row only.
    private const int ColumnLimit = 40;

    /// <summary>
    /// How the session routes and what it holds, over the rows it counts.
    /// </summary>
    public static string Summary(SessionReport report)
    {
        var mode = Mode(report);
        var counts = report.Held == 0 ? Loc.Instance.Get("Check_SessionsNone") : Counts(report);
        return mode.Length == 0 ? counts : mode + "\n" + counts;
    }

    /// <summary>
    /// The destinations as rows, freshest first.
    /// </summary>
    public static List<LiveRowItem> Cards(SessionReport report)
    {
        var rows = new List<LiveRowItem>(report.Sessions.Count);
        foreach (var row in report.Sessions)
        {
            rows.Add(new LiveRowItem(row.Host, row.Name, row.Route, Way(row), Why(row), Left(row), Carried(row),
                row.IdleSeconds, row.Stalled));
        }

        return [.. rows.OrderBy(row => row.Idle < 0 ? int.MaxValue : row.Idle)
            .ThenBy(row => Key(row.Host), StringComparer.Ordinal)];
    }

    /// <summary>
    /// The rows as a padded table, for the viewer, the clipboard and the export. A column no row fills is left
    /// out.
    /// </summary>
    public static string Text(IReadOnlyList<LiveRowItem> rows)
    {
        var host = Column(rows.Select(row => row.Host));
        var name = Column(rows.Select(row => row.Name));
        var way = Column(rows.Select(row => row.WayText));
        var why = Column(rows.Select(row => row.Why));
        var left = Column(rows.Select(row => row.Left));
        var text = new StringBuilder();
        foreach (var row in rows)
        {
            text.Append(row.Host.PadRight(host));
            if (name > 0)
            {
                text.Append(row.Name.PadRight(name));
            }

            text.Append(row.WayText.PadRight(way)).Append(row.Why.PadRight(why));
            if (left > 0)
            {
                text.Append(row.Left.PadRight(left));
            }

            text.Append(row.Detail).Append('\n');
        }

        return text.ToString();
    }

    // How the session routes, said once over the rows instead of on every one of them.
    private static string Mode(SessionReport report)
    {
        return report.Mode switch
        {
            SessionReport.ModeSplit => report.List.Length > 0
                ? Loc.Instance.Get("Check_ModeSplitList", report.List)
                : Loc.Instance.Get("Check_ModeSplit"),
            SessionReport.ModeFull => report.List.Length > 0
                ? Loc.Instance.Get("Check_ModeFullList", report.List)
                : Loc.Instance.Get("Check_ModeFull"),
            SessionReport.ModeOff => Loc.Instance.Get("Check_ModeOff"),
            _ => string.Empty,
        };
    }

    // What is held, by where it goes.
    private static string Counts(SessionReport report)
    {
        var parts = new List<string>
        {
            Loc.Instance.Get("Check_SessionsSummary", report.Held, report.Tunnel, report.Direct, report.Block,
                report.Undecided),
        };
        if (report.TotalBytes > 0)
        {
            parts.Add(Loc.Instance.Get("Check_SessionsMoved", CheckFormat.Bytes(report.TotalBytes)));
        }

        if (report.Stalled > 0)
        {
            parts.Add(Loc.Instance.Get("Check_SessionsStalled", report.Stalled));
        }

        return string.Join(" \u00b7 ", parts);
    }

    // Width of a column: its longest value plus a gap, or nothing when no row carries it.
    private static int Column(IEnumerable<string> values)
    {
        var longest = 0;
        foreach (var value in values)
        {
            longest = Math.Max(longest, value.Length);
        }

        return longest == 0 ? 0 : Math.Min(longest, ColumnLimit) + 2;
    }

    // An address read as text puts 10.x ahead of 9.x, so each part is padded to the width it is compared at.
    private static string Key(string host)
    {
        if (!IPAddress.TryParse(host, out _))
        {
            return host;
        }

        var parts = host.Split('.');
        return parts.Length == 4 ? string.Join('.', parts.Select(part => part.PadLeft(3, '0'))) : host;
    }

    // Where the destination goes, in the window's language.
    private static string Way(LiveSession row)
    {
        return row.Route switch
        {
            LiveSession.PathTunnel => Loc.Instance.Get("Check_Verdict_proxy"),
            LiveSession.PathDirect => Loc.Instance.Get("Check_Verdict_direct"),
            LiveSession.PathBlock => Loc.Instance.Get("Check_Verdict_block"),
            _ => Loc.Instance.Get("Check_Verdict_undecided"),
        };
    }

    // What settled the destination, in the window's language.
    private static string Why(LiveSession row)
    {
        return row.Reason switch
        {
            LiveSession.ReasonRange => Loc.Instance.Get("Check_Why_range"),
            LiveSession.ReasonName => Loc.Instance.Get("Check_Why_name"),
            LiveSession.ReasonApp => Loc.Instance.Get("Check_Why_app"),
            LiveSession.ReasonResolved => Loc.Instance.Get("Check_Why_resolved"),
            LiveSession.ReasonService => Loc.Instance.Get("Check_Why_service"),
            LiveSession.ReasonConfig => Loc.Instance.Get("Check_Why_config"),
            _ => Loc.Instance.Get("Check_Why_none"),
        };
    }

    // How long the destination has before it is forgotten; a standing range has no clock.
    private static string Left(LiveSession row)
    {
        return row.LeftSeconds < 0 ? string.Empty : Loc.Instance.Get("Check_SessionLeft", row.LeftSeconds);
    }

    // What the destination holds beyond its verdict: the application it belongs to and what has moved there.
    private static string Carried(LiveSession row)
    {
        var parts = new List<string>();
        if (row.App.Length > 0)
        {
            parts.Add(row.App);
        }

        if (row.Bytes > 0)
        {
            parts.Add(CheckFormat.Bytes(row.Bytes));
        }

        if (row.BitsPerSecond >= 0)
        {
            parts.Add(Loc.Instance.Get("Check_Rate", CheckFormat.Mbits(row.BitsPerSecond)));
        }

        if (row.Live > 0)
        {
            parts.Add(Loc.Instance.Get("Check_SessionLive", row.Live));
        }

        if (row.Stalled)
        {
            parts.Add(Loc.Instance.Get("Check_SessionStalled"));
        }

        return string.Join(" \u00b7 ", parts);
    }
}
