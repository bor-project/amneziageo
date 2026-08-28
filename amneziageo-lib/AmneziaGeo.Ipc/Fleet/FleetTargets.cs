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
}
