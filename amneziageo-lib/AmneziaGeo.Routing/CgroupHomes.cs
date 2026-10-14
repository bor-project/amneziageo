using System.Globalization;

namespace AmneziaGeo.Routing;

/// <summary>
/// Reads the cgroup a process lived in before an app rule took it, and picks the one it goes back to.
/// </summary>
public static class CgroupHomes
{
    private const int Generations = 64;

    /// <summary>
    /// Returns the path of the unified cgroup in the text of /proc/&lt;pid&gt;/cgroup, or null when it names none.
    /// </summary>
    public static string? Of(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith("0::", StringComparison.Ordinal))
            {
                var path = line[3..].Trim();

                return path.StartsWith('/') ? path : null;
            }
        }

        return null;
    }

    /// <summary>
    /// Returns the parent of a process in the text of /proc/&lt;pid&gt;/stat, or null when it cannot be read.
    /// </summary>
    public static int? Parent(string stat)
    {
        ArgumentNullException.ThrowIfNull(stat);

        var close = stat.LastIndexOf(')');
        if (close < 0)
        {
            return null;
        }

        var fields = stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return fields.Length > 1 && int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parent)
            ? parent
            : null;
    }

    /// <summary>
    /// Returns the cgroup a process goes back to: its own, else the one of its nearest forebear the rule took, else
    /// null.
    /// </summary>
    public static string? Back(int pid, IReadOnlyDictionary<int, string> homes, Func<int, int?> parent)
    {
        ArgumentNullException.ThrowIfNull(homes);
        ArgumentNullException.ThrowIfNull(parent);

        var at = pid;
        for (var step = 0; step < Generations; step++)
        {
            if (homes.TryGetValue(at, out var home))
            {
                return home;
            }

            if (parent(at) is not { } up || up <= 1 || up == at)
            {
                return null;
            }

            at = up;
        }

        return null;
    }
}
