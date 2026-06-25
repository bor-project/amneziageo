using System;
using System.Diagnostics;
using System.IO;

namespace AmneziaGeo.Windows.Ui.Services;

/// <summary>
/// Client-side resolution of where the agent stores wg-quick configs, mirroring
/// AmneziaGeo.Windows.App.TunnelPaths. CommonApplicationData (%ProgramData%) is machine-global, so the
/// client computes the same path the LocalSystem agent writes to - used only to reveal a file in Explorer.
/// </summary>
internal static class ConfigPaths
{
    private static string ConfigurationsDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "AmneziaGeo",
            "Configurations");
    }

    /// <summary>Full path to a stored config's .conf file.</summary>
    public static string ConfigFile(string name)
    {
        return Path.Combine(ConfigurationsDirectory(), $"{name}.conf");
    }

    /// <summary>
    /// Opens Explorer with the config's file selected (or, if it is missing, its folder). Best-effort:
    /// swallows failures so a missing Explorer / path never crashes the UI.
    /// </summary>
    public static void RevealInExplorer(string name)
    {
        try
        {
            var path = ConfigFile(name);
            var argument = File.Exists(path)
                ? $"/select,\"{path}\""
                : $"\"{ConfigurationsDirectory()}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", argument) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // best-effort reveal; nothing actionable if Explorer can't be launched.
        }
    }
}
