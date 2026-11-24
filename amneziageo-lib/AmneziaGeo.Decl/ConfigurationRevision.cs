using System.Security.Cryptography;
using System.Text;

namespace AmneziaGeo.Decl;

public static class ConfigurationRevision
{
    /// <summary>
    /// Returns the revision of a configuration.
    /// </summary>
    public static string Of(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(text))));

    /// <summary>
    /// Returns the changed field names.
    /// </summary>
    public static IReadOnlyList<string> Fields(string before, string after)
    {
        if (Of(before) == Of(after))
        {
            return [];
        }

        var left = Values(before);
        var right = Values(after);
        var changed = left.Keys.Union(right.Keys, StringComparer.Ordinal)
            .Where(key => left.GetValueOrDefault(key) != right.GetValueOrDefault(key))
            .Order(StringComparer.Ordinal).ToArray();
        return changed.Length > 0 ? changed : ["Configuration"];
    }

    /// <summary>
    /// Identifies changes that require coordinated connection settings.
    /// </summary>
    public static bool RequiresCoordination(IReadOnlyList<string> fields) => fields.Any(one => !Compatible.Contains(one));

    private static readonly HashSet<string> Compatible = new(StringComparer.OrdinalIgnoreCase)
    {
        "[interface].DNS", "[interface].MTU", "[interface].Jc", "[interface].Jmin", "[interface].Jmax",
        "[interface].I1", "[interface].I2", "[interface].I3", "[interface].I4", "[interface].I5",
        "[interface].RekeyAfterTime", "[interface].RekeyTimeout", "[interface].RejectAfterTime",
        "[interface].KeepaliveTimeout", "[interface].MaxHandshakeAttempts",
        "[peer]1.AllowedIPs", "[peer]1.PersistentKeepalive",
    };

    private static string Normalize(string text)
    {
        var lines = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }
            var separator = line.IndexOf('=');
            lines.Add(line.StartsWith('[') ? line.ToLowerInvariant()
                : line.StartsWith('#') || separator <= 0 ? line
                : line[..separator].Trim().ToLowerInvariant() + " = "
                    + string.Join(", ", line[(separator + 1)..].Split(',').Select(one => one.Trim())));
        }
        return string.Join('\n', lines);
    }

    private static Dictionary<string, string> Values(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var section = string.Empty;
        var peer = 0;
        foreach (var raw in Normalize(text).Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line;
                if (line.Equals("[Peer]", StringComparison.OrdinalIgnoreCase))
                {
                    section += (++peer).ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0 || line.StartsWith('#') && !line.StartsWith("# AmneziaGeo ", StringComparison.Ordinal))
            {
                continue;
            }

            var key = section + "." + line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            values[key] = values.TryGetValue(key, out var previous) ? previous + "\n" + value : value;
        }
        return values;
    }
}
