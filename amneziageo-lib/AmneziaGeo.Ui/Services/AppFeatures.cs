using System;
using System.IO;

namespace AmneziaGeo.Ui.Services;

/// <summary>
/// Runtime feature switches driven by marker files next to the UI state, in %LOCALAPPDATA%\AmneziaGeo.
/// </summary>
internal static class AppFeatures
{
    // Read once per process: create or delete the marker, then restart the app.
    private static readonly Lazy<bool> DebugMarker = new(() => Exists("DEBUG"));

    /// <summary>
    /// Whether the per-app tunneling controls are shown. Off until the feature is stable; the DEBUG marker
    /// turns it back on for testing.
    /// </summary>
    public static bool PerAppRouting => DebugMarker.Value;

    /// <summary>
    /// Whether the several-servers switch is offered. Windows carries the mode; the other platforms get it
    /// once the port catches up.
    /// </summary>
    public static bool MultiServer => OperatingSystem.IsWindows();

    private static bool Exists(string marker)
    {
        try
        {
            return File.Exists(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AmneziaGeo",
                marker));
        }
        catch
        {
            return false;
        }
    }
}
