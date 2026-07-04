using Microsoft.Win32;
using AmneziaGeo.Localization;

namespace AmneziaGeo.Windows.Ui.ViewModels;

/// <summary>
/// Enumerates installed applications from the Windows "Uninstall" registry for the per-app tunneling picker. Read in the UI process so both per-machine (HKLM, 64- and 32-bit) and per-user (HKCU) installs are visible; the agent runs as SYSTEM and would miss the user's HKCU.
/// </summary>
internal static class InstalledApps
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string UninstallPathWow = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>
    /// Returns the installed apps as picker candidates (an app:dir= / app:path= token each), de-duplicated and sorted by name. An unreadable key is skipped rather than failing the enumeration.
    /// </summary>
    public static IReadOnlyList<AppCandidate> List()
    {
        var result = new List<AppCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        ReadHive(Registry.LocalMachine, UninstallPath, result, seen);
        ReadHive(Registry.LocalMachine, UninstallPathWow, result, seen);
        ReadHive(Registry.CurrentUser, UninstallPath, result, seen);

        result.Sort((a, b) => string.Compare(a.Display, b.Display, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    private static void ReadHive(RegistryKey hive, string path, List<AppCandidate> result, HashSet<string> seen)
    {
        using var root = hive.OpenSubKey(path);
        if (root is null)
        {
            return;
        }

        foreach (var subName in root.GetSubKeyNames())
        {
            try
            {
                using var key = root.OpenSubKey(subName);
                if (key is null)
                {
                    continue;
                }

                var display = key.GetValue("DisplayName") as string;
                if (string.IsNullOrWhiteSpace(display))
                {
                    continue; // updates / components with no friendly name
                }

                // Skip OS components, patches, and updates under a parent product.
                if ((key.GetValue("SystemComponent") as int? ?? 0) == 1
                    || key.GetValue("ParentKeyName") is not null
                    || key.GetValue("ParentDisplayName") is not null)
                {
                    continue;
                }

                var token = ResolveToken(key);
                if (token is null || !seen.Add(token))
                {
                    continue;
                }

                result.Add(new AppCandidate(Loc.Instance.Get("InstalledApps_Installed", display.Trim()), token));
            }
            catch
            {
                // Unreadable / malformed entry: skip it.
            }
        }
    }

    // Prefer the install folder; fall back to the main exe from DisplayIcon.
    private static string? ResolveToken(RegistryKey key)
    {
        if (key.GetValue("InstallLocation") as string is { } location && !string.IsNullOrWhiteSpace(location))
        {
            return $"app:dir={location.Trim().Trim('"').TrimEnd('\\', '/')}";
        }

        var exe = ExeFromDisplayIcon(key.GetValue("DisplayIcon") as string);
        return exe is null ? null : $"app:path={exe}";
    }

    // Strip the icon index and quotes from DisplayIcon; accept only a real .exe.
    private static string? ExeFromDisplayIcon(string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon))
        {
            return null;
        }

        var value = icon.Trim().Trim('"');
        var comma = value.LastIndexOf(',');
        if (comma > 0 && value.Length - comma <= 5)
        {
            value = value[..comma];
        }

        value = value.Trim().Trim('"');
        return value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? value : null;
    }
}
