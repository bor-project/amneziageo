using System.Net;
using System.Text;

using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;

namespace AmneziaGeo.Ui.ViewModels;

/// <summary>
/// One destination the tunnel holds, as the table shows it: the address, the name it was resolved by, the path it
/// takes and what is left on it.
/// </summary>
internal sealed record LiveRowItem(string Host, string Name, string Route, string Detail, int Idle, bool Stalled)
{
    /// <summary>
    /// The row as one line, for the clipboard.
    /// </summary>
    public string Line => string.Join("  ", Host, Name.Length == 0 ? "-" : Name, Route, Detail);
}

/// <summary>
/// Renders what the tunnel carries: the line that counts it, the rows of the table and the same rows as text.
/// </summary>
internal static class SessionRows
{
    /// <summary>
    /// Order by destination address.
    /// </summary>
    public const string ByHost = "host";

    /// <summary>
    /// Order by the name the destination was resolved by.
    /// </summary>
    public const string ByName = "name";

    /// <summary>
    /// Order by the path the destination takes.
    /// </summary>
    public const string ByPath = "path";

    /// <summary>
    /// Order by how long the destination has been idle, which is what the state column carries.
    /// </summary>
    public const string ByState = "state";

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
    /// The destinations as rows, in the order the table asks for.
    /// </summary>
    public static List<LiveRowItem> Cards(SessionReport report, string sort = ByState, bool descending = false)
    {
        var rows = new List<LiveRowItem>(report.Sessions.Count);
        foreach (var row in report.Sessions)
        {
            rows.Add(new LiveRowItem(row.Host, row.Name, Way(row), Carried(row), Math.Max(row.IdleSeconds, 0), row.Stalled));
        }

        var sorted = Sorted(rows, sort);
        return descending ? [.. sorted.Reverse()] : [.. sorted];
    }

    /// <summary>
    /// The same rows as a padded table, for the clipboard and the export.
    /// </summary>
    public static string Text(SessionReport report, string sort = ByState, bool descending = false)
    {
        var text = new StringBuilder();
        foreach (var row in Cards(report, sort, descending))
        {
            text.Append(row.Host.PadRight(20))
                .Append((row.Name.Length == 0 ? "-" : row.Name).PadRight(30))
                .Append(row.Route.PadRight(14))
                .Append(row.Detail)
                .Append('\n');
        }

        return text.ToString();
    }

    // A row without a name sorts after the rows that carry one; everything else sorts by its own column.
    private static IEnumerable<LiveRowItem> Sorted(List<LiveRowItem> rows, string sort)
    {
        return sort switch
        {
            ByHost => rows.OrderBy(row => Key(row.Host), StringComparer.Ordinal),
            ByName => rows.OrderBy(row => row.Name.Length == 0).ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase),
            ByPath => rows.OrderBy(row => row.Route, StringComparer.CurrentCultureIgnoreCase).ThenBy(row => row.Idle),
            _ => rows.OrderBy(row => row.Idle).ThenBy(row => Key(row.Host), StringComparer.Ordinal),
        };
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
