using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Seeds the per-user data root from the legacy machine-wide ProgramData store.
/// </summary>
internal static class DataMigration
{
    private const string AppFolder = "AmneziaGeo";

    /// <summary>
    /// Copies the legacy ProgramData data tree into the per-user root when the user root has no database.
    /// </summary>
    public static void SeedFromProgramData(ILogger logger)
    {
        var target = AppDataRoot.Base();
        var source = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), AppFolder);
        if (PathsEqual(source, target))
        {
            return;
        }

        if (File.Exists(Path.Combine(target, "state.db")))
        {
            return;
        }

        if (!File.Exists(Path.Combine(source, "state.db")))
        {
            return;
        }

        try
        {
            CopyTree(new DirectoryInfo(source), new DirectoryInfo(target));
            logger.LogInformation("seeded user data from legacy store {Source} -> {Target}", source, target);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "seed user data failed: {Source} -> {Target}", source, target);
        }
    }

    private static void CopyTree(DirectoryInfo source, DirectoryInfo target)
    {
        // Subdirectories before loose files, so the state.db sentinel lands only after the tree it references.
        Directory.CreateDirectory(target.FullName);
        foreach (var dir in source.EnumerateDirectories())
        {
            CopyTree(dir, new DirectoryInfo(Path.Combine(target.FullName, dir.Name)));
        }

        foreach (var file in source.EnumerateFiles())
        {
            var dest = Path.Combine(target.FullName, file.Name);
            if (!File.Exists(dest))
            {
                file.CopyTo(dest);
            }
        }
    }

    private static bool PathsEqual(string a, string b)
    {
        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            StringComparison.OrdinalIgnoreCase);
    }
}
