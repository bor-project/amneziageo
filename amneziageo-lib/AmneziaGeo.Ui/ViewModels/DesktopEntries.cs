using System.Globalization;
using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;

namespace AmneziaGeo.Ui.ViewModels;

/// <summary>
/// Enumerates installed applications from the desktop entries for the per-app tunneling picker. Read in the UI process so the entries of the user stand next to those of the whole machine; the agent runs as root and would miss the user's own.
/// </summary>
internal static class DesktopEntries
{
    /// <summary>
    /// Returns the installed apps as picker candidates (an app:path= token each), de-duplicated and sorted by name. An unreadable entry is skipped rather than failing the enumeration.
    /// </summary>
    public static IReadOnlyList<AppCandidate> List()
    {
        var result = new List<AppCandidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var directory in Directories())
        {
            Read(directory, result, seen);
        }

        result.Sort((a, b) => string.Compare(a.Display, b.Display, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    // Where the entries of the user and of the machine stand.
    private static IEnumerable<string> Directories()
    {
        var home = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        yield return Path.Combine(
            string.IsNullOrWhiteSpace(home)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share")
                : home.Trim(),
            "applications");

        var shared = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
        foreach (var directory in (string.IsNullOrWhiteSpace(shared) ? "/usr/local/share:/usr/share" : shared)
            .Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            yield return Path.Combine(directory.Trim(), "applications");
        }
    }

    private static void Read(string directory, List<AppCandidate> result, HashSet<string> seen)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        var language = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        foreach (var file in Directory.EnumerateFiles(directory, "*.desktop"))
        {
            try
            {
                if (DesktopEntry.Read(File.ReadAllLines(file), language) is not { } entry)
                {
                    continue;
                }

                // This application is not offered: a rule on it tunnels the agent's own traffic.
                var token = "app:path=" + entry.Image;
                if (OwnAppRule.Names(token) || !seen.Add(token))
                {
                    continue;
                }

                result.Add(new AppCandidate(Loc.Instance.Get("InstalledApps_Installed", entry.Name), token));
            }
            catch (Exception)
            {
                // Unreadable / malformed entry: skip it.
            }
        }
    }
}
