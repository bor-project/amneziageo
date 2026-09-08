using AmneziaGeo.Ipc;

namespace AmneziaGeo.Linux.App;

/// <summary>
/// Enumerates the running programs for the per-application picker: one row per executable, named the way a rule
/// names it.
/// </summary>
internal static class ProcessCatalog
{
    /// <summary>
    /// A picker row.
    /// </summary>
    public sealed record Entry(string Kind, string Label, string Value, string Detail);

    /// <summary>
    /// Returns the distinct executables behind the processes of this machine.
    /// </summary>
    public static IReadOnlyList<Entry> List()
    {
        var entries = new List<Entry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(directory), out _) || ImageOf(directory) is not { } image)
            {
                continue;
            }

            // This application is not offered: a rule on it tunnels the agent's own traffic.
            if (!seen.Add(image) || OwnAppRule.Names("app:path=" + image))
            {
                continue;
            }

            entries.Add(new Entry("app", Path.GetFileName(image), image, string.Empty));
        }

        entries.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));
        return entries;
    }

    // The executable a process runs, or null for a kernel thread and for one that left mid-walk.
    private static string? ImageOf(string directory)
    {
        try
        {
            var target = File.ResolveLinkTarget($"{directory}/exe", returnFinalTarget: true)?.FullName;
            return string.IsNullOrEmpty(target) || !Path.IsPathRooted(target) ? null : target;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
