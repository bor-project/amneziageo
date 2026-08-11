using System.Text;

using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;

namespace AmneziaGeo.Ui.ViewModels;

/// <summary>
/// One destination the relay holds, as a narrow window shows it.
/// </summary>
internal sealed record LiveRowItem(string Host, string Verdict, string Detail, bool Stalled);

/// <summary>
/// Renders what the relay carries: the line that counts it, a padded table for a wide window and cards for a
/// narrow one.
/// </summary>
internal static class SessionRows
{
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
    /// The destinations as text, busiest first: where each one goes and what it holds.
    /// </summary>
    public static string Text(SessionReport report)
    {
        var text = new StringBuilder();
        foreach (var row in report.Sessions)
        {
            text.Append(row.Host.PadRight(34))
                .Append(Word(row.Verdict).PadRight(16))
                .Append(Carried(row))
                .Append('\n');
        }

        return text.ToString();
    }

    /// <summary>
    /// The same destinations as cards.
    /// </summary>
    public static List<LiveRowItem> Cards(SessionReport report)
    {
        var rows = new List<LiveRowItem>(report.Sessions.Count);
        foreach (var row in report.Sessions)
        {
            rows.Add(new LiveRowItem(row.Host, Word(row.Verdict), Carried(row), row.Stalled));
        }

        return rows;
    }

    // What one destination holds: how much has gone there, how fast it moves now and how long it has been idle.
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

    // The verdict in the window's language; an agent that says something else says it in its own words.
    private static string Word(string verdict)
    {
        return verdict is "proxy" or "direct" or "block" or LiveSession.Undecided
            ? Loc.Instance.Get($"Check_Verdict_{verdict}")
            : verdict;
    }
}
