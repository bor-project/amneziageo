using System.Net;
using System.Text;

using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;

namespace AmneziaGeo.Ui.ViewModels;

/// <summary>
/// One destination the tunnel holds: the address, the name it was resolved by, the path it takes and what is left
/// on it.
/// </summary>
internal sealed record LiveRowItem(string Host, string Name, string Route, string Detail, int Idle, bool Stalled)
{
    /// <summary>
    /// The row as one line, for the clipboard.
    /// </summary>
    public string Line => Name.Length == 0
        ? string.Join("  ", Host, Route, Detail)
        : string.Join("  ", Host, Name, Route, Detail);
}

/// <summary>
/// Renders what the tunnel carries: the line that counts it, the rows behind it and the same rows as text.
/// </summary>
internal static class SessionRows
{
    // Widest a column is padded to; a longer value pushes the rest of its own row only.
    private const int ColumnLimit = 40;

    /// <summary>
    /// Counts what is held, or says nothing is going.
    /// </summary>
    public static string Summary(SessionReport report)
    {
        return report.Held == 0
            ? Loc.Instance.Get("Check_SessionsNone")
            : Loc.Instance.Get("Check_SessionsSummary", report.Held, report.Undecided, report.Stalled,
                CheckFormat.Bytes(report.TotalBytes));
    }

    /// <summary>
    /// The destinations as rows, freshest first.
    /// </summary>
    public static List<LiveRowItem> Cards(SessionReport report)
    {
        var rows = new List<LiveRowItem>(report.Sessions.Count);
        foreach (var row in report.Sessions)
        {
            rows.Add(new LiveRowItem(row.Host, row.Name, Way(row), Carried(row), Math.Max(row.IdleSeconds, 0), row.Stalled));
        }

        return [.. rows.OrderBy(row => row.Idle).ThenBy(row => Key(row.Host), StringComparer.Ordinal)];
    }

    /// <summary>
    /// The same rows as a padded table, for the viewer, the clipboard and the export. A column no row fills is
    /// left out.
    /// </summary>
    public static string Text(SessionReport report)
    {
        var rows = Cards(report);
        var host = Column(rows.Select(row => row.Host));
        var name = Column(rows.Select(row => row.Name));
        var route = Column(rows.Select(row => row.Route));
        var text = new StringBuilder();
        foreach (var row in rows)
        {
            text.Append(row.Host.PadRight(host));
            if (name > 0)
            {
                text.Append(row.Name.PadRight(name));
            }

            text.Append(row.Route.PadRight(route)).Append(row.Detail).Append('\n');
        }

        return text.ToString();
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

    // What one destination holds: which rule it goes by, how much has gone there, how fast it moves now and how
    // long it has been idle.
    private static string Carried(LiveSession row)
    {
        var parts = new List<string>();
        if (row.Verdict == LiveSession.Undecided)
        {
            parts.Add(Loc.Instance.Get("Check_SessionNoRule"));
        }

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

        if (row.AgeSeconds >= 0)
        {
            parts.Add(Loc.Instance.Get("Check_SessionAge", row.AgeSeconds));
        }

        if (row.IdleSeconds >= 0)
        {
            parts.Add(Loc.Instance.Get("Check_SessionIdle", row.IdleSeconds));
        }

        if (row.Stalled)
        {
            parts.Add(Loc.Instance.Get("Check_SessionStalled"));
        }

        return parts.Count == 0 ? "-" : string.Join(" · ", parts);
    }
}
