using System.Globalization;

namespace AmneziaGeo.Ipc.Fleet;

/// <summary>
/// Where every addressed rule of the routing lists rides. A rule that leaves the choice to the machine is not
/// held here at all, so a mode nobody has addressed a rule in stores nothing.
/// </summary>
public static class FleetTargets
{
    /// <summary>
    /// Nothing addressed.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, RuleRoute> Empty =
        new Dictionary<string, RuleRoute>(StringComparer.Ordinal);

    /// <summary>
    /// Nothing addressed, in the words the snapshot carries them by.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Unaddressed =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// The key a rule is addressed by: the list it belongs to and the token it is written as.
    /// </summary>
    public static string Key(long listId, string token)
    {
        return $"{listId}:{token.Trim()}";
    }

    /// <summary>
    /// Reads a key back into the list it belongs to and the token it was written as.
    /// </summary>
    public static bool TrySplit(string key, out long listId, out string token)
    {
        token = string.Empty;
        var cut = key.IndexOf(':');
        if (cut <= 0 || !long.TryParse(key[..cut], NumberStyles.Integer, CultureInfo.InvariantCulture, out listId))
        {
            listId = 0;
            return false;
        }

        token = key[(cut + 1)..].Trim();
        return token.Length > 0;
    }

    /// <summary>
    /// Writes the addressed rules as they are stored.
    /// </summary>
    public static string Format(IReadOnlyDictionary<string, RuleRoute> targets)
    {
        var lines = new List<string>();
        foreach (var pair in targets.OrderBy(target => target.Key, StringComparer.Ordinal))
        {
            var key = pair.Key.Trim();
            if (key.Length > 0 && !pair.Value.IsDefault)
            {
                lines.Add($"{key}={pair.Value.Format()}");
            }
        }

        return string.Join('\n', lines);
    }

    /// <summary>
    /// Reads the stored rules.
    /// </summary>
    public static IReadOnlyDictionary<string, RuleRoute> Parse(string? text)
    {
        var targets = new Dictionary<string, RuleRoute>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(text))
        {
            return targets;
        }

        foreach (var line in text.Split('\n'))
        {
            // Neither end carries an "=", so the last one ends the key; an application rule carries its own.
            var cut = line.LastIndexOf('=');
            if (cut <= 0)
            {
                continue;
            }

            var key = line[..cut].Trim();
            var route = RuleRoute.Parse(line[(cut + 1)..]);
            if (key.Length > 0 && !route.IsDefault)
            {
                targets[key] = route;
            }
        }

        return targets;
    }

    /// <summary>
    /// Drops the addresses of the rules a list no longer sends into a tunnel; answers whether any went.
    /// </summary>
    public static bool KeepRules(IDictionary<string, RuleRoute> targets, long listId, IReadOnlySet<string> tokens)
    {
        var dropped = false;
        foreach (var key in targets.Keys.ToArray())
        {
            if (TrySplit(key, out var id, out var token) && id == listId && !tokens.Contains(token))
            {
                targets.Remove(key);
                dropped = true;
            }
        }

        return dropped;
    }

    /// <summary>
    /// Leaves both ends naming a server that is gone to the machine; answers whether any moved.
    /// </summary>
    public static bool ForgetServer(IDictionary<string, RuleRoute> targets, string name)
    {
        return Strike(targets, server => string.Equals(server, name, StringComparison.Ordinal));
    }

    /// <summary>
    /// Leaves both ends naming a server outside <paramref name="known"/> to the machine; answers whether any moved.
    /// </summary>
    public static bool KeepServers(IDictionary<string, RuleRoute> targets, IReadOnlySet<string> known)
    {
        return Strike(targets, server => !known.Contains(server));
    }

    /// <summary>
    /// The servers named at either end of an addressed rule.
    /// </summary>
    public static IReadOnlySet<string> Servers(IReadOnlyDictionary<string, RuleRoute> targets)
    {
        var servers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var route in targets.Values)
        {
            foreach (var end in new[] { route.Target, route.Fallback })
            {
                if (end.Mode == RuleTarget.Server && !string.IsNullOrEmpty(end.Name))
                {
                    servers.Add(end.Name);
                }
            }
        }

        return servers;
    }

    // Leaves both ends naming a struck server to the machine; answers whether any moved.
    private static bool Strike(IDictionary<string, RuleRoute> targets, Func<string, bool> struck)
    {
        var moved = false;
        foreach (var key in targets.Keys.ToArray())
        {
            var route = targets[key];
            var kept = new RuleRoute(
                Struck(route.Target, struck) ? RuleTarget.Default : route.Target,
                Struck(route.Fallback, struck) ? RuleTarget.Default : route.Fallback);
            if (kept == route)
            {
                continue;
            }

            moved = true;
            if (kept.IsDefault)
            {
                targets.Remove(key);
            }
            else
            {
                targets[key] = kept;
            }
        }

        return moved;
    }

    /// <summary>
    /// Names a renamed server as it is called now at both ends; answers whether any moved.
    /// </summary>
    public static bool RenameServer(IDictionary<string, RuleRoute> targets, string oldName, string newName)
    {
        var moved = false;
        foreach (var key in targets.Keys.ToArray())
        {
            var route = targets[key];
            var renamed = new RuleRoute(
                Names(route.Target, oldName) ? new RuleTarget(RuleTarget.Server, newName) : route.Target,
                Names(route.Fallback, oldName) ? new RuleTarget(RuleTarget.Server, newName) : route.Fallback);
            if (renamed == route)
            {
                continue;
            }

            targets[key] = renamed;
            moved = true;
        }

        return moved;
    }

    private static bool Names(RuleTarget end, string name)
    {
        return end.Mode == RuleTarget.Server && string.Equals(end.Name, name, StringComparison.Ordinal);
    }

    private static bool Struck(RuleTarget end, Func<string, bool> struck)
    {
        return end.Mode == RuleTarget.Server && struck(end.Name);
    }
}
