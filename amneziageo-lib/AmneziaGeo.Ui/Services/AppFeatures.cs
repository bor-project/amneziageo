using System;

namespace AmneziaGeo.Ui.Services;

/// <summary>
/// Runtime feature switches of the window.
/// </summary>
internal static class AppFeatures
{
    /// <summary>
    /// Whether the several-servers switch is offered. Windows carries the mode; the other platforms get it
    /// once the port catches up.
    /// </summary>
    public static bool MultiServer => OperatingSystem.IsWindows();
}
