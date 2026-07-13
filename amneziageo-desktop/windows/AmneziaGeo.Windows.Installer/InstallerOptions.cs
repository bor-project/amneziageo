using System;
using System.IO;
using System.Text.Json;

namespace AmneziaGeo.Windows.Installer;

/// <summary>
/// Installer choices persisted per user so they carry across installs (#183). Saved next to the app's
/// ui-prefs.json in %LOCALAPPDATA%\AmneziaGeo. The destructive reset/delete-config choice is deliberately
/// not persisted - it stays opt-in per run.
/// </summary>
public sealed class InstallerOptions
{
    /// <summary>
    /// Create a desktop shortcut.
    /// </summary>
    public bool DesktopShortcut { get; set; } = true;

    /// <summary>
    /// Create a Start-menu shortcut.
    /// </summary>
    public bool StartMenuShortcut { get; set; } = true;

    /// <summary>
    /// Launch the app after a successful install/update.
    /// </summary>
    public bool LaunchAfter { get; set; } = true;

    /// <summary>
    /// Dial the existing connection right after the post-install launch.
    /// </summary>
    public bool AutoConnect { get; set; }

    /// <summary>
    /// Download the geo databases after install/update/repair.
    /// </summary>
    public bool DownloadLists { get; set; } = true;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AmneziaGeo",
        "installer-options.json");

    /// <summary>
    /// Loads the saved options, or defaults when absent.
    /// </summary>
    public static InstallerOptions Load()
    {
        try
        {
            return JsonSerializer.Deserialize<InstallerOptions>(File.ReadAllText(FilePath)) ?? new InstallerOptions();
        }
        catch
        {
            return new InstallerOptions();
        }
    }

    /// <summary>
    /// Writes the options back.
    /// </summary>
    public void Save()
    {
        try
        {
            var path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this));
        }
        catch
        {
        }
    }
}
