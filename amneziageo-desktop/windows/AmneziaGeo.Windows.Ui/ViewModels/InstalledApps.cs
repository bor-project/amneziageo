using Microsoft.Win32;

namespace AmneziaGeo.Windows.Ui.ViewModels;

/// <summary>
/// Enumerates installed applications from the Windows "Uninstall" registry (the same source as
/// Add/Remove Programs) for the per-app tunneling picker (#71). Read in the UI process, which runs as the
/// user, so both per-machine (HKLM, 64- and 32-bit) and per-user (HKCU) installs are visible - the agent
/// runs as SYSTEM and would miss the user's HKCU (where per-user apps register).
/// </summary>
internal static class InstalledApps
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string UninstallPathWow = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>
    /// Returns the installed apps as picker candidates (an app:dir= / app:path= token each), de-duplicated
    /// and sorted by name. Best-effort: an unreadable key is skipped rather than failing the enumeration.
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

                // Skip OS components, patches, and updates under a parent product - they are not apps.
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

                result.Add(new AppCandidate($"{display.Trim()} · установлено", token));
            }
            catch
            {
                // Unreadable / malformed entry: skip it.
            }
        }
    }

    // Prefer the install folder (catches the app's helpers / versioned subfolders); fall back to the main
    // exe from DisplayIcon. Returns null when neither yields a usable path.
    private static string? ResolveToken(RegistryKey key)
    {
        if (key.GetValue("InstallLocation") as string is { } location && !string.IsNullOrWhiteSpace(location))
        {
            return $"app:dir={location.Trim().Trim('"').TrimEnd('\\', '/')}";
        }

        var exe = ExeFromDisplayIcon(key.GetValue("DisplayIcon") as string);
        return exe is null ? null : $"app:path={exe}";
    }

    // DisplayIcon is usually "<exe>" or "<exe>,<index>"; strip the icon index and quotes, accept only a
    // real .exe (not a .dll or a bare icon resource).
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
