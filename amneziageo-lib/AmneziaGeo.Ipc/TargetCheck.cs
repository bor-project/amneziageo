using System.Globalization;
using System.Text;

namespace AmneziaGeo.Ipc;

/// <summary>
/// What a targeted check is asked about.
/// </summary>
public static class CheckTargetKind
{
    /// <summary>
    /// A name, resolved before it is judged.
    /// </summary>
    public const string Domain = "domain";

    /// <summary>
    /// A bare address.
    /// </summary>
    public const string Address = "address";

    /// <summary>
    /// An application, by package or by image path.
    /// </summary>
    public const string App = "app";

    /// <summary>
    /// A geo category.
    /// </summary>
    public const string Geo = "geo";
}

/// <summary>
/// One thing found about the target. Kind is the column it belongs to, state colours it, detail carries the value.
/// </summary>
public sealed record CheckFact(string Kind, string Name, string State, string Detail = "")
{
    /// <summary>
    /// Renders the fact as one protocol row.
    /// </summary>
    public string ToRow()
    {
        return string.Join('\t', "fact", Kind, Name, State, Detail.Replace('\t', ' '));
    }

    /// <summary>
    /// Reads a fact back from its protocol row.
    /// </summary>
    public static CheckFact? TryParse(string row)
    {
        var parts = row.Split('\t');
        return parts.Length >= 4 && parts[0] == "fact"
            ? new CheckFact(parts[1], parts[2], parts[3], parts.Length > 4 ? parts[4] : string.Empty)
            : null;
    }
}

/// <summary>
/// Verdict keys for a targeted check.
/// </summary>
public static class TargetVerdicts
{
    /// <summary>
    /// No routing list is in force, so nothing is decided per destination.
    /// </summary>
    public const string NoRules = "Check_TargetNoRules";

    /// <summary>
    /// The name has no address, so nothing can be routed. Args: the name.
    /// </summary>
    public const string Unresolved = "Check_TargetUnresolved";

    /// <summary>
    /// A block rule drops it. Args: the rule.
    /// </summary>
    public const string Blocked = "Check_TargetBlocked";

    /// <summary>
    /// A direct rule keeps it out of the tunnel. Args: the rule.
    /// </summary>
    public const string Direct = "Check_TargetDirect";

    /// <summary>
    /// A proxy rule puts it in the tunnel. Args: the rule.
    /// </summary>
    public const string Proxy = "Check_TargetProxy";

    /// <summary>
    /// In the tunnel by rule, and still nothing answers. Args: the address.
    /// </summary>
    public const string Unreachable = "Check_TargetUnreachable";

    /// <summary>
    /// The application is in no list, so its traffic follows the default. Args: the application.
    /// </summary>
    public const string AppUnlisted = "Check_TargetAppUnlisted";

    /// <summary>
    /// An app rule covers it, but it talks to bare addresses no rule covers - the rule cannot catch them.
    /// Args: the application, the number of such addresses.
    /// </summary>
    public const string AppBareIp = "Check_TargetAppBareIp";

    /// <summary>
    /// The tunnel carries only the named applications and this one is not among them, so no rule reaches it.
    /// Args: the application, how many applications are named.
    /// </summary>
    public const string AppOutside = "Check_TargetAppOutside";

    /// <summary>
    /// A proxy rule carries it, but only for the named applications. Args: the rule, how many are named.
    /// </summary>
    public const string ProxyAppsOnly = "Check_TargetProxyAppsOnly";

    /// <summary>
    /// No rule covers it and the tunnel carries only what is listed: it leaves through the physical path.
    /// </summary>
    public const string UnlistedSplit = "Check_TargetUnlistedSplit";

    /// <summary>
    /// No rule covers it and the tunnel is the default: it rides the tunnel anyway.
    /// </summary>
    public const string UnlistedFull = "Check_TargetUnlistedFull";
}

/// <summary>
/// What the app rules of the active list do on the platform that answers the check.
/// </summary>
public enum AppScope
{
    /// <summary>
    /// The list names no application, or the platform passes app rules over.
    /// </summary>
    None,

    /// <summary>
    /// An app rule adds the addresses its application reaches, and every other rule keeps deciding as it did.
    /// </summary>
    Additive,

    /// <summary>
    /// Only the named applications reach the tunnel; every other one leaves beside it, past every rule.
    /// </summary>
    Exclusive,
}

/// <summary>
/// What the agent found about the target, in the terms the verdict is decided by.
/// </summary>
public sealed record TargetFindings(
    string Kind,
    bool Split,
    bool RoutingActive,
    string MatchedRule = "",
    RoleToken Role = RoleToken.None,
    int Addresses = 0,
    int Unlisted = 0,
    bool Resolved = true,
    bool Reachable = true,
    string AppRule = "",
    AppScope Apps = AppScope.None,
    int AppCount = 0);

/// <summary>
/// A finished targeted check: everything found about one address, name, application or category, and the phrase
/// that says why its traffic goes where it goes.
/// </summary>
public sealed record TargetReport(
    long UnixMs,
    string Config,
    string Target,
    IReadOnlyList<CheckFact> Facts,
    string VerdictKey,
    IReadOnlyList<string> VerdictArgs)
{
    /// <summary>
    /// Renders the check as the ack payload: one row per fact, then the verdict.
    /// </summary>
    public string ToPayload()
    {
        var rows = new List<string> { string.Join('\t', "target", Target) };
        foreach (var fact in Facts)
        {
            rows.Add(fact.ToRow());
        }

        var verdict = new StringBuilder("verdict\t\t").Append(VerdictKey);
        foreach (var arg in VerdictArgs)
        {
            verdict.Append('\t').Append(arg);
        }

        rows.Add(verdict.ToString());
        return string.Join('\n', rows);
    }

    /// <summary>
    /// Reads a check back from the ack payload.
    /// </summary>
    public static TargetReport Parse(string payload, long unixMs = 0, string config = "")
    {
        var facts = new List<CheckFact>();
        var target = string.Empty;
        var key = TargetVerdicts.NoRules;
        var args = new List<string>();
        foreach (var row in payload.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (CheckFact.TryParse(row) is { } fact)
            {
                facts.Add(fact);
                continue;
            }

            var parts = row.Split('\t');
            if (parts.Length >= 2 && parts[0] == "target")
            {
                target = parts[1];
            }
            else if (parts.Length >= 3 && parts[0] == "verdict")
            {
                key = parts[2];
                args = [.. parts.Skip(3)];
            }
        }

        return new TargetReport(unixMs, config, target, facts, key, args);
    }

    /// <summary>
    /// Renders the check in English for the agent log and the support archive.
    /// </summary>
    public string Render()
    {
        var text = new StringBuilder();
        var stamp = UnixMs > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(UnixMs).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : "-";
        text.Append("target check \"").Append(Target).Append('"')
            .Append(Config.Length > 0 ? $" on \"{Config}\"" : string.Empty)
            .Append(" at ").Append(stamp).Append('\n');
        foreach (var fact in Facts)
        {
            text.Append("  ").Append(fact.Kind.PadRight(10)).Append(fact.Name.PadRight(28)).Append(fact.State.PadRight(10))
                .Append(fact.Detail).Append('\n');
        }

        text.Append("  verdict   ").Append(TargetPhrase.English(VerdictKey, VerdictArgs)).Append('\n');
        return text.ToString();
    }
}

/// <summary>
/// Turns what was found about a target into the phrase that says why its traffic goes where it goes.
/// </summary>
public static class TargetVerdict
{
    /// <summary>
    /// Decides a targeted check.
    /// </summary>
    public static (string Key, IReadOnlyList<string> Args) Decide(TargetFindings found, string target)
    {
        if (!found.RoutingActive)
        {
            return (TargetVerdicts.NoRules, []);
        }

        if (!found.Resolved)
        {
            return (TargetVerdicts.Unresolved, [target]);
        }

        if (found.Role == RoleToken.Block)
        {
            return (TargetVerdicts.Blocked, [found.MatchedRule]);
        }

        var named = found.AppCount.ToString(CultureInfo.InvariantCulture);
        if (found.Kind == CheckTargetKind.App)
        {
            if (found.AppRule.Length == 0)
            {
                return found.Apps == AppScope.Exclusive
                    ? (TargetVerdicts.AppOutside, [target, named])
                    : (TargetVerdicts.AppUnlisted, [target]);
            }

            if (found.Unlisted > 0)
            {
                return (TargetVerdicts.AppBareIp, [target, found.Unlisted.ToString(CultureInfo.InvariantCulture)]);
            }
        }

        if (found.Role == RoleToken.Direct)
        {
            return (TargetVerdicts.Direct, [found.MatchedRule]);
        }

        if (found.Role == RoleToken.Proxy)
        {
            if (!found.Reachable)
            {
                return (TargetVerdicts.Unreachable, [target]);
            }

            return found.Apps == AppScope.Exclusive
                ? (TargetVerdicts.ProxyAppsOnly, [found.MatchedRule, named])
                : (TargetVerdicts.Proxy, [found.MatchedRule]);
        }

        return found.Split ? (TargetVerdicts.UnlistedSplit, [target]) : (TargetVerdicts.UnlistedFull, [target]);
    }
}

/// <summary>
/// Role names the findings carry, spelled the same on every platform.
/// </summary>
public enum RoleToken
{
    /// <summary>
    /// No rule covers the target.
    /// </summary>
    None,

    /// <summary>
    /// A proxy rule covers it.
    /// </summary>
    Proxy,

    /// <summary>
    /// A direct rule covers it.
    /// </summary>
    Direct,

    /// <summary>
    /// A block rule covers it.
    /// </summary>
    Block,
}

/// <summary>
/// The English wording of a targeted verdict, for the log and the support archive.
/// </summary>
public static class TargetPhrase
{
    /// <summary>
    /// Renders a verdict key with its arguments.
    /// </summary>
    public static string English(string key, IReadOnlyList<string> args)
    {
        return key switch
        {
            TargetVerdicts.NoRules => "no routing list is in force, so every destination follows the tunnel's own AllowedIPs",
            TargetVerdicts.Unresolved => $"\"{Arg(args, 0)}\" resolves to no address, so nothing can be routed for it: the resolver is the fault",
            TargetVerdicts.Blocked => $"dropped by the block rule \"{Arg(args, 0)}\"",
            TargetVerdicts.Direct => $"kept out of the tunnel by the direct rule \"{Arg(args, 0)}\"",
            TargetVerdicts.Proxy => $"carried by the tunnel under the rule \"{Arg(args, 0)}\"",
            TargetVerdicts.Unreachable => $"in the tunnel by rule and still nothing answers at {Arg(args, 0)}: the fault is past the tunnel",
            TargetVerdicts.AppUnlisted => $"\"{Arg(args, 0)}\" is in no list, so its traffic follows the default route",
            TargetVerdicts.AppOutside => $"\"{Arg(args, 0)}\" is named by no app rule, and the tunnel carries only the {Arg(args, 1)} application(s) that are: it leaves beside the tunnel, past every rule in the list",
            TargetVerdicts.ProxyAppsOnly => $"carried by the tunnel under the rule \"{Arg(args, 0)}\", but only for the {Arg(args, 1)} named application(s): every other application leaves beside the tunnel",
            TargetVerdicts.AppBareIp => $"\"{Arg(args, 0)}\" talks to {Arg(args, 1)} address(es) no rule covers: an app rule is reactive and never catches a bare address, so add a geoip rule for the service",
            TargetVerdicts.UnlistedSplit => $"no rule covers \"{Arg(args, 0)}\" and the tunnel carries only what is listed, so it leaves through the physical path",
            TargetVerdicts.UnlistedFull => $"no rule covers \"{Arg(args, 0)}\" and the tunnel is the default route, so it rides the tunnel",
            _ => "nothing was found about this target",
        };
    }

    private static string Arg(IReadOnlyList<string> args, int index)
    {
        return index < args.Count ? args[index] : "?";
    }
}
