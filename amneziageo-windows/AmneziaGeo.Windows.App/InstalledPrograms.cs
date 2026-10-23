using AmneziaGeo.Ipc;
using Microsoft.Win32;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Lists the programs of the machine the way a search of app:&lt;name&gt; sees them: the Uninstall entries of both
/// registry views and of the signed-in user, the App Paths, and the shortcuts of the Start menu of the machine and of
/// the user.
/// </summary>
internal static class InstalledPrograms
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private const string AppPathsPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";

    private const string ProfileListPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";

    private const string StartMenuPath = @"Microsoft\Windows\Start Menu\Programs";

    // How deep the folders of the Start menu are walked.
    private const int MenuDepth = 4;

    /// <summary>
    /// Returns the programs of the machine and of the user with this SID; an entry that cannot be read is left out.
    /// </summary>
    public static IReadOnlyList<NamedApp> List(string? sid)
    {
        var profile = Profile(sid);
        var programs = new List<NamedApp>();
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            Uninstall(machine, UninstallPath, null, programs);
            AppPaths(machine, AppPathsPath, null, programs);
        }

        if (sid is { Length: > 0 })
        {
            Uninstall(Registry.Users, $@"{sid}\{UninstallPath}", profile, programs);
            AppPaths(Registry.Users, $@"{sid}\{AppPathsPath}", profile, programs);
        }

        Shortcuts(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), StartMenuPath), programs);
        if (profile is not null)
        {
            Shortcuts(Path.Combine(profile, "AppData", "Roaming", StartMenuPath), programs);
        }

        return programs;
    }

    // The products an Uninstall key names: each with its program, else with its install folder.
    private static void Uninstall(RegistryKey hive, string path, string? profile, List<NamedApp> programs)
    {
        using var root = Open(hive, path);
        foreach (var subName in root?.GetSubKeyNames() ?? [])
        {
            try
            {
                using var key = root!.OpenSubKey(subName);
                if (key?.GetValue("DisplayName") is not string { Length: > 0 } display
                    || (key.GetValue("SystemComponent") as int? ?? 0) == 1
                    || key.GetValue("ParentKeyName") is not null)
                {
                    continue;
                }

                var exe = ExeFromIcon(Text(key, "DisplayIcon", profile));
                var folder = Text(key, "InstallLocation", profile)?.Trim('"').TrimEnd('\\', '/');
                if (exe is not null)
                {
                    programs.Add(new NamedApp(display.Trim(), exe, string.Empty, "path=" + exe));
                }
                else if (folder is { Length: > 0 })
                {
                    programs.Add(new NamedApp(display.Trim(), string.Empty, string.Empty, "dir=" + folder));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // An entry that cannot be read stays out of the list.
            }
        }
    }

    // The programs App Paths registers under their file names.
    private static void AppPaths(RegistryKey hive, string path, string? profile, List<NamedApp> programs)
    {
        using var root = Open(hive, path);
        foreach (var subName in root?.GetSubKeyNames() ?? [])
        {
            try
            {
                using var key = root!.OpenSubKey(subName);
                if (key is not null && Text(key, string.Empty, profile)?.Trim('"') is { Length: > 0 } exe
                    && exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    programs.Add(new NamedApp(string.Empty, exe, string.Empty, "path=" + exe));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // An entry that cannot be read stays out of the list.
            }
        }
    }

    // The programs the shortcuts of a Start menu point at, each under the name of its shortcut.
    private static void Shortcuts(string folder, List<NamedApp> programs)
    {
        try
        {
            if (!Directory.Exists(folder))
            {
                return;
            }

            var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = MenuDepth, IgnoreInaccessible = true };
            foreach (var file in Directory.EnumerateFiles(folder, "*.lnk", options))
            {
                if (ShellLink.Target(file) is { } target && target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    programs.Add(new NamedApp(Path.GetFileNameWithoutExtension(file), target, string.Empty, "path=" + target));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A menu that cannot be read stays out of the list.
        }
    }

    // The profile folder of the user with this SID, null when the machine names none.
    private static string? Profile(string? sid)
    {
        if (sid is not { Length: > 0 })
        {
            return null;
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{ProfileListPath}\{sid}");
            return key?.GetValue("ProfileImagePath") is string { Length: > 0 } path ? Environment.ExpandEnvironmentVariables(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    // A value of a key with the folders of the user put in place of their variables, the rest of them as the machine
    // expands them.
    private static string? Text(RegistryKey key, string name, string? profile)
    {
        if (key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string { Length: > 0 } raw)
        {
            return null;
        }

        var text = raw.Trim();
        if (profile is not null)
        {
            text = text
                .Replace("%LOCALAPPDATA%", Path.Combine(profile, "AppData", "Local"), StringComparison.OrdinalIgnoreCase)
                .Replace("%APPDATA%", Path.Combine(profile, "AppData", "Roaming"), StringComparison.OrdinalIgnoreCase)
                .Replace("%USERPROFILE%", profile, StringComparison.OrdinalIgnoreCase);
        }

        return Environment.ExpandEnvironmentVariables(text);
    }

    // The program a DisplayIcon names, without its icon index and quotes; only an .exe.
    private static string? ExeFromIcon(string? icon)
    {
        if (icon is not { Length: > 0 })
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

    private static RegistryKey? Open(RegistryKey hive, string path)
    {
        try
        {
            return hive.OpenSubKey(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }
}
