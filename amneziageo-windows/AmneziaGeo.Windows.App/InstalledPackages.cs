using System.Xml.Linq;
using AmneziaGeo.Ipc;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// A Store/MSIX package installed here: its family, its publisher and the programs it runs.
/// </summary>
internal readonly record struct PackageEntry(string Family, string Publisher, IReadOnlyList<string> Executables);

/// <summary>
/// Lists the Store/MSIX packages installed on this machine, reading the WindowsApps folder.
/// </summary>
internal static class InstalledPackages
{
    /// <summary>
    /// Returns one entry per installed package family; the programs are read only for the named publishers, a manifest each.
    /// </summary>
    public static IReadOnlyList<PackageEntry> Snapshot(IReadOnlySet<string> publishers, ILogger logger)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
        var byFamily = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in Folders(root, logger))
        {
            var family = AppPathToken.PackageFamilyFromFullName(Path.GetFileName(folder));
            if (family is null)
            {
                continue;
            }

            if (!byFamily.TryGetValue(family, out var executables))
            {
                executables = [];
                byFamily[family] = executables;
            }

            if (publishers.Contains(PublisherOf(family)))
            {
                executables.AddRange(Executables(folder, logger));
            }
        }

        return byFamily.Select(entry => new PackageEntry(entry.Key, PublisherOf(entry.Key), entry.Value)).ToList();
    }

    // Package folders; a WindowsApps closed to this account yields none.
    private static IReadOnlyList<string> Folders(string root, ILogger logger)
    {
        try
        {
            return Directory.GetDirectories(root);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "the installed Store apps could not be listed, so app rules naming one are left unchecked");
            return [];
        }
    }

    // The programs a package declares in its manifest.
    private static IReadOnlyList<string> Executables(string folder, ILogger logger)
    {
        try
        {
            var manifest = XDocument.Load(Path.Combine(folder, "AppxManifest.xml"));
            return manifest.Descendants()
                .Where(element => element.Name.LocalName == "Application")
                .Select(element => element.Attribute("Executable")?.Value)
                .Where(value => !string.IsNullOrEmpty(value))
                .Select(value => value!)
                .ToList();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "the manifest of {Folder} could not be read, so the programs it runs stay unknown", folder);
            return [];
        }
    }

    // "Name_PublisherId" -> "PublisherId".
    private static string PublisherOf(string family)
    {
        var underscore = family.LastIndexOf('_');
        return underscore > 0 ? family[(underscore + 1)..] : string.Empty;
    }
}
