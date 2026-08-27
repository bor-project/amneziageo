namespace AmneziaGeo.Ipc.Fleet;

/// <summary>
/// What the mode stands on: the servers in the order it lists them, the role each holds, the one that carries
/// the machine and the ones wanted up.
/// </summary>
/// <param name="Order">Servers in the order the mode lists them.</param>
/// <param name="Roles">Role of every server given one.</param>
/// <param name="Primary">Server that carries what no rule sends elsewhere; empty while none is named.</param>
/// <param name="Desired">Servers wanted up.</param>
public sealed record FleetState(
    IReadOnlyList<string> Order,
    IReadOnlyDictionary<string, string> Roles,
    string Primary,
    IReadOnlyList<string> Desired)
{
    /// <summary>
    /// A mode never entered.
    /// </summary>
    public static readonly FleetState Empty =
        new([], new Dictionary<string, string>(StringComparer.Ordinal), string.Empty, []);

    /// <summary>
    /// Writes names as they are stored, one per line.
    /// </summary>
    public static string FormatNames(IEnumerable<string> names)
    {
        return string.Join('\n', Distinct(names));
    }

    /// <summary>
    /// Reads stored names.
    /// </summary>
    public static IReadOnlyList<string> ParseNames(string? text)
    {
        return string.IsNullOrEmpty(text) ? [] : Distinct(text.Split('\n'));
    }

    /// <summary>
    /// Writes the roles as they are stored.
    /// </summary>
    public static string FormatRoles(IReadOnlyDictionary<string, string> roles)
    {
        var lines = new List<string>();
        foreach (var pair in roles.OrderBy(role => role.Key, StringComparer.Ordinal))
        {
            var name = Clean(pair.Key);
            if (name.Length > 0)
            {
                lines.Add($"{name}={TunnelRoles.Of(pair.Value)}");
            }
        }

        return string.Join('\n', lines);
    }

    /// <summary>
    /// Reads the stored roles.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ParseRoles(string? text)
    {
        var roles = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(text))
        {
            return roles;
        }

        foreach (var line in text.Split('\n'))
        {
            // A role token carries no "=", so the last one ends the name.
            var cut = line.LastIndexOf('=');
            if (cut <= 0)
            {
                continue;
            }

            var name = Clean(line[..cut]);
            var role = line[(cut + 1)..];
            if (name.Length > 0 && TunnelRoles.IsKnown(role))
            {
                roles[name] = TunnelRoles.Of(role);
            }
        }

        return roles;
    }

    private static List<string> Distinct(IEnumerable<string> names)
    {
        var kept = new List<string>();
        foreach (var candidate in names)
        {
            var name = Clean(candidate);
            if (name.Length > 0 && !kept.Contains(name, StringComparer.Ordinal))
            {
                kept.Add(name);
            }
        }

        return kept;
    }

    private static string Clean(string? value)
    {
        return value?.Trim() ?? string.Empty;
    }
}
