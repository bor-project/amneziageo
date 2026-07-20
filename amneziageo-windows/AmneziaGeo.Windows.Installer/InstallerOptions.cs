using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using AmneziaGeo.Dal;

namespace AmneziaGeo.Windows.Installer;

/// <summary>
/// Installer choices persisted per user so they carry across installs (#183). Stored in the shared
/// state.db in %LOCALAPPDATA%\AmneziaGeo. The destructive reset/delete-config choice is deliberately
/// not persisted - it stays opt-in per run.
/// </summary>
public sealed class InstallerOptions
{
    private const string Scope = "installer";

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

    private static string DbPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AmneziaGeo",
        "state.db");

    private static string LegacyFilePath => Path.Combine(
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
            var store = new LocalKeyValueStore(DbPath);
            var values = store.Load(Scope);
            if (values.Count == 0 && TryImportLegacy(store, out var imported))
            {
                return imported;
            }

            return FromValues(values);
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
            Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
            new LocalKeyValueStore(DbPath).Save(Scope, ToValues());
        }
        catch
        {
        }
    }

    private Dictionary<string, string> ToValues()
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["desktop-shortcut"] = Bool(DesktopShortcut),
            ["start-menu-shortcut"] = Bool(StartMenuShortcut),
            ["launch-after"] = Bool(LaunchAfter),
            ["auto-connect"] = Bool(AutoConnect),
            ["download-lists"] = Bool(DownloadLists),
        };
    }

    private static InstallerOptions FromValues(IReadOnlyDictionary<string, string> values)
    {
        var options = new InstallerOptions();
        options.DesktopShortcut = ReadBool(values, "desktop-shortcut", options.DesktopShortcut);
        options.StartMenuShortcut = ReadBool(values, "start-menu-shortcut", options.StartMenuShortcut);
        options.LaunchAfter = ReadBool(values, "launch-after", options.LaunchAfter);
        options.AutoConnect = ReadBool(values, "auto-connect", options.AutoConnect);
        options.DownloadLists = ReadBool(values, "download-lists", options.DownloadLists);
        return options;
    }

    // One-time import of the pre-DB installer-options.json, then delete it so the settings folder is left clean.
    private static bool TryImportLegacy(LocalKeyValueStore store, out InstallerOptions options)
    {
        options = new InstallerOptions();
        var path = LegacyFilePath;
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            var legacy = JsonSerializer.Deserialize<InstallerOptions>(File.ReadAllText(path));
            if (legacy is null)
            {
                return false;
            }

            options = legacy;
            store.Save(Scope, options.ToValues());
            TryDelete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool ReadBool(IReadOnlyDictionary<string, string> values, string key, bool fallback)
    {
        return values.TryGetValue(key, out var value) ? string.Equals(value, "true", StringComparison.Ordinal) : fallback;
    }

    private static string Bool(bool value)
    {
        return value ? "true" : "false";
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
