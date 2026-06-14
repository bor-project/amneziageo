namespace AmneziaGeo.Windows.Ui.Services;

/// <summary>
/// Locates the AmneziaGeo.Windows.App executable for privileged operations.
/// </summary>
internal static class AppLocator
{
    /// <summary>
    /// Returns the path to the app executable, or null if it cannot be found.
    /// </summary>
    public static string? Locate()
    {
        var baseDir = AppContext.BaseDirectory;
        var inPlace = Path.Combine(baseDir, "AmneziaGeo.Windows.App.exe");
        if (File.Exists(inPlace))
        {
            return inPlace;
        }

        var config = new DirectoryInfo(baseDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Parent?.Name ?? "Debug";
        var dir = new DirectoryInfo(baseDir);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "AmneziaGeo.Windows.App")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            return null;
        }

        var candidate = Path.Combine(dir.FullName, "AmneziaGeo.Windows.App", "bin", config, "net10.0", "AmneziaGeo.Windows.App.exe");
        return File.Exists(candidate) ? candidate : null;
    }
}
