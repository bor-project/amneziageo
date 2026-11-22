using System.Runtime.CompilerServices;
using AmneziaGeo.Ui.Services;

namespace AmneziaGeo.Tests;

internal static class TestStorage
{
    internal static string Root { get; private set; } = string.Empty;

    [ModuleInitializer]
    internal static void Initialize()
    {
        var productionRoot = Path.GetFullPath(UiPreferences.StorageDirectory);
        var root = Directory.CreateTempSubdirectory("AmneziaGeo.Tests-").FullName;
        var marker = Path.Combine(root, ".test-storage");
        var identity = Guid.NewGuid().ToString("N");
        File.WriteAllText(marker, identity);
        UiPreferences.StorageDirectory = root;

        var preferences = new UiPreferences();
        foreach (var path in new[] { preferences.DbPath, preferences.LegacyFilePath, preferences.LegacyMarkerPath })
        {
            if (!string.Equals(Path.GetDirectoryName(path), root, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetDirectoryName(path), productionRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("UI test storage is not isolated.");
            }
        }

        Root = root;
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                if (Directory.GetParent(root)?.FullName == Path.TrimEndingDirectorySeparator(Path.GetTempPath())
                    && Path.GetFileName(root).StartsWith("AmneziaGeo.Tests-", StringComparison.Ordinal)
                    && File.ReadAllText(marker) == identity)
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine(ex);
            }
        };
    }
}
