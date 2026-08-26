using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace AmneziaGeo.Ipc;

/// <summary>
/// Where the distributor sends one rule and what led there.
/// </summary>
/// <param name="Rule">The rule as it is written.</param>
/// <param name="Kind">Where the matches go: auto, server, direct or block.</param>
/// <param name="Server">The configuration carrying them; empty where no tunnel does.</param>
/// <param name="Reason">What led there, in the words the journal writes.</param>
public sealed record RuleLayout(string Rule, string Kind, string Server, string Reason);

/// <summary>
/// What one server was left carrying.
/// </summary>
/// <param name="Server">The configuration.</param>
/// <param name="List">The routing list it carries.</param>
/// <param name="Rules">Rules riding it.</param>
/// <param name="Carrier">Whether it carries everything no other server is named for.</param>
public sealed record ServerLayout(string Server, string List, int Rules, bool Carrier);

/// <summary>
/// How the distributor split the routing list across the servers up right now: where every proxied rule came out
/// and what each server was left with. Rules of the Direct and Block roles stay out of it - they decide the same
/// way wherever they land - and with several servers off nothing is split at all: each tunnel carries the whole
/// list its own configuration is bound to.
/// </summary>
/// <param name="MultiServer">Whether several servers work at once.</param>
/// <param name="List">The list the whole machine routes through; empty with the mode off.</param>
/// <param name="Servers">The servers up, priority top down.</param>
/// <param name="Rules">Every proxied rule and where it came out.</param>
/// <param name="Direct">Rules sent past the tunnel.</param>
/// <param name="Blocked">Rules dropped rather than leaked.</param>
public sealed record RoutingLayout(
    bool MultiServer,
    string List,
    IReadOnlyList<ServerLayout> Servers,
    IReadOnlyList<RuleLayout> Rules,
    int Direct,
    int Blocked)
{
    /// <summary>
    /// What the layout is of: one list across the fleet, or a tunnel apiece. Written from the rest of the
    /// answer, so it does not travel with it.
    /// </summary>
    [JsonIgnore]
    public string Headline
    {
        get
        {
            if (!MultiServer)
            {
                return "several servers are off: each tunnel carries the whole list it is bound to";
            }

            return List.Length > 0
                ? $"list '{List}' across {Count(Servers.Count)} server(s) up"
                : "no routing list is selected: every tunnel carries everything";
        }
    }

    /// <summary>
    /// Renders the layout in English for the diagnostics screen and the support archive.
    /// </summary>
    public string Render()
    {
        var text = new StringBuilder(Headline).Append('\n');
        foreach (var server in Servers)
        {
            text.Append("  ").Append(Column(server.Server, 22))
                .Append(Column(server.List, 18))
                .Append(Column(Count(server.Rules) + " rule(s)", 14))
                .Append(server.Carrier ? "carries everything besides" : string.Empty)
                .Append('\n');
        }

        if (Rules.Count > 0)
        {
            text.Append('\n');
        }

        foreach (var rule in Rules)
        {
            text.Append("  ").Append(Column(rule.Rule, 34))
                .Append(Column(rule.Kind, 8))
                .Append(Column(rule.Server, 18))
                .Append(rule.Reason).Append('\n');
        }

        if (Direct + Blocked > 0)
        {
            text.Append("\n  ").Append(Count(Direct)).Append(" rule(s) go past the tunnel, ")
                .Append(Count(Blocked)).Append(" dropped\n");
        }

        return text.ToString();
    }

    private static string Count(int value)
    {
        return value.ToString(CultureInfo.InvariantCulture);
    }

    // A column an empty value still holds its width in.
    private static string Column(string value, int width)
    {
        return (value.Length == 0 ? "-" : value).PadRight(width);
    }
}
