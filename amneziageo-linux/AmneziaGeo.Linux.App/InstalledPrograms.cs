using AmneziaGeo.Ipc;

namespace AmneziaGeo.Linux.App;

/// <summary>
/// Lists the programs of the machine the way a search of app:&lt;name&gt; sees them: the desktop entries of the
/// machine, of every user, of flatpak and of snap, and the programs of the path; each by the file it runs, past the
/// links to it and to its folders.
/// </summary>
internal static class InstalledPrograms
{
    private const string Applications = "applications";

    // The folders the programs of the path stand in beside the ones the agent is started with.
    private static readonly string[] _pathFolders = ["/usr/local/sbin", "/usr/local/bin", "/usr/sbin", "/usr/bin", "/sbin", "/bin", "/snap/bin"];

    /// <summary>
    /// Returns the programs of the machine; an entry or a folder that cannot be read is left out.
    /// </summary>
    public static IReadOnlyList<NamedApp> List()
    {
        var programs = new List<NamedApp>();
        foreach (var folder in EntryFolders())
        {
            Entries(folder, programs);
        }

        foreach (var folder in PathFolders())
        {
            Executables(folder, programs);
        }

        return programs;
    }

    // Where desktop entries stand: the machine, flatpak, snap and the home of every user.
    private static IEnumerable<string> EntryFolders()
    {
        var shared = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
        foreach (var folder in (string.IsNullOrWhiteSpace(shared) ? "/usr/local/share:/usr/share" : shared).Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            yield return Path.Combine(folder.Trim(), Applications);
        }

        yield return "/usr/local/share/" + Applications;
        yield return "/usr/share/" + Applications;
        yield return "/var/lib/flatpak/exports/share/" + Applications;
        yield return "/var/lib/snapd/desktop/" + Applications;
        yield return "/root/.local/share/" + Applications;
        foreach (var home in Folders("/home"))
        {
            yield return Path.Combine(home, ".local", "share", Applications);
            yield return Path.Combine(home, ".local", "share", "flatpak", "exports", "share", Applications);
        }
    }

    private static IEnumerable<string> PathFolders()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        return path.Split(':', StringSplitOptions.RemoveEmptyEntries).Select(folder => folder.Trim().TrimEnd('/'))
            .Concat(_pathFolders)
            .Where(folder => folder.StartsWith('/'))
            .Distinct(StringComparer.Ordinal);
    }

    // The programs the desktop entries of a folder start, each under the name of its entry.
    private static void Entries(string folder, List<NamedApp> programs)
    {
        foreach (var file in Files(folder, "*.desktop"))
        {
            try
            {
                if (DesktopEntry.Read(File.ReadAllLines(file), string.Empty) is { } entry)
                {
                    var image = Resolved(entry.Image);
                    programs.Add(new NamedApp(entry.Name, image, string.Empty, "path=" + image));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An entry that cannot be read stays out of the list.
            }
        }
    }

    // The programs of a folder of the path, each by its file name.
    private static void Executables(string folder, List<NamedApp> programs)
    {
        foreach (var file in Files(folder, "*"))
        {
            try
            {
                if (!File.GetUnixFileMode(file).HasFlag(UnixFileMode.UserExecute))
                {
                    continue;
                }

                var image = Resolved(file);
                programs.Add(new NamedApp(string.Empty, file, string.Empty, "path=" + image));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file that cannot be read stays out of the list.
            }
        }
    }

    // How many links to folders one path may lead through.
    private const int FolderLinks = 40;

    // The file a path runs once its links and the links to its folders are followed.
    private static string Resolved(string path)
    {
        try
        {
            var file = File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path;
            var folder = Path.GetDirectoryName(file);

            return folder is { Length: > 0 } ? Path.Combine(RealFolder(folder, FolderLinks), Path.GetFileName(file)) : file;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return path;
        }
    }

    // The folder with every link along it followed.
    private static string RealFolder(string folder, int links)
    {
        var real = "/";
        foreach (var part in folder.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Path.GetFullPath(Path.Combine(real, part));
            var target = new DirectoryInfo(next).ResolveLinkTarget(returnFinalTarget: true)?.FullName;
            if (target is null)
            {
                real = next;
            }
            else if (links > 0)
            {
                real = RealFolder(target, links - 1);
            }
            else
            {
                throw new IOException($"too many links to folders on {folder}");
            }
        }

        return real;
    }

    private static IEnumerable<string> Files(string folder, string pattern)
    {
        try
        {
            return Directory.Exists(folder) ? Directory.GetFiles(folder, pattern) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IEnumerable<string> Folders(string folder)
    {
        try
        {
            return Directory.Exists(folder) ? Directory.GetDirectories(folder) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
