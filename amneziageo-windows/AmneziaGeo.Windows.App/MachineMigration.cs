using AmneziaGeo.Decl;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Moves machine-wide assets and shared rows out of the legacy per-user store into the machine store.
/// </summary>
internal static class MachineMigration
{
    private const string MigratedKey = "machine-migrated";
    private static readonly string[] MachineFolders = ["geo", "logs", "diagnostics"];
    private static readonly string[] StatePatterns = ["dns-state*.txt", "route-state*.txt", "lan-state*.txt"];

    /// <summary>
    /// Copies geo bases, logs, and tunnel runtime state from the legacy per-user root into the machine root when
    /// absent. File operations only; runs before any logging opens the shared log database.
    /// </summary>
    public static void SeedMachineFolders()
    {
        try
        {
            var source = AppDataRoot.Base();
            var target = AppDataRoot.MachineBase();
            if (PathsEqual(source, target) || !Directory.Exists(source))
            {
                return;
            }

            Directory.CreateDirectory(target);
            foreach (var folder in MachineFolders)
            {
                CopyDirIfAbsent(Path.Combine(source, folder), Path.Combine(target, folder));
            }

            foreach (var pattern in StatePatterns)
            {
                if (!Directory.Exists(source))
                {
                    continue;
                }

                foreach (var file in Directory.EnumerateFiles(source, pattern))
                {
                    CopyFileIfAbsent(file, Path.Combine(target, Path.GetFileName(file)));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Copies the shared geo sources, geo file metadata, and machine settings from a legacy user store into the
    /// machine store once.
    /// </summary>
    public static async Task SplitLegacyAsync(IStateStore machine, IStateStore user, ILogger logger, CancellationToken ct = default)
    {
        try
        {
            if (!string.IsNullOrEmpty(await machine.GetSettingAsync(MigratedKey, ct)))
            {
                return;
            }

            var sources = await user.ListGeoSourcesAsync(ct);
            foreach (var source in sources)
            {
                await machine.SaveGeoSourceAsync(source, ct);
            }

            foreach (var file in await user.ListGeoFilesAsync(ct))
            {
                await machine.SaveGeoFileAsync(file, ct);
            }

            foreach (var key in ScopedStateStore.MachineKeys)
            {
                var value = await user.GetSettingAsync(key, ct);
                if (value is not null)
                {
                    await machine.SetSettingAsync(key, value, ct);
                }
            }

            await machine.SetSettingAsync(MigratedKey, "1", ct);
            logger.LogInformation("{Sources} rule database(s) and the machine-wide settings were moved to shared storage, so every user of this computer now uses the same ones", sources.Count);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "the rule databases and machine-wide settings could not be moved to shared storage; they stay per-user and the move is tried again next start");
        }
    }

    private static void CopyDirIfAbsent(string source, string target)
    {
        if (!Directory.Exists(source))
        {
            return;
        }

        Directory.CreateDirectory(target);
        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            CopyDirIfAbsent(dir, Path.Combine(target, Path.GetFileName(dir)));
        }

        foreach (var file in Directory.EnumerateFiles(source))
        {
            CopyFileIfAbsent(file, Path.Combine(target, Path.GetFileName(file)));
        }
    }

    private static void CopyFileIfAbsent(string source, string target)
    {
        if (!File.Exists(target))
        {
            File.Copy(source, target);
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
