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
    /// Copies geo bases, logs, and diagnostics from the legacy per-user root into the machine root while it has none
    /// of its own, and moves the legacy tunnel runtime state there once. File operations only; runs before any
    /// logging opens the shared log database.
    /// </summary>
    public static void SeedMachineFolders()
    {
        Seed(AppDataRoot.Base(), AppDataRoot.MachineBase());
    }

    /// <summary>
    /// Seeds the machine root <paramref name="target"/> from the legacy root <paramref name="source"/>.
    /// </summary>
    internal static void Seed(string source, string target)
    {
        try
        {
            if (PathsEqual(source, target) || !Directory.Exists(source))
            {
                return;
            }

            Directory.CreateDirectory(target);
            foreach (var folder in MachineFolders)
            {
                var machine = Path.Combine(target, folder);
                // Only a folder the machine root does not have yet.
                if (!Directory.Exists(machine))
                {
                    CopyDir(Path.Combine(source, folder), machine);
                }
            }

            foreach (var pattern in StatePatterns)
            {
                foreach (var file in Directory.EnumerateFiles(source, pattern))
                {
                    MoveOrDrop(file, Path.Combine(target, Path.GetFileName(file)));
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

    private static void CopyDir(string source, string target)
    {
        if (!Directory.Exists(source))
        {
            return;
        }

        Directory.CreateDirectory(target);
        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            CopyDir(dir, Path.Combine(target, Path.GetFileName(dir)));
        }

        foreach (var file in Directory.EnumerateFiles(source))
        {
            if (!IsTransactionFile(file))
            {
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
            }
        }
    }

    // The write-ahead log and shared-memory index of a database.
    private static bool IsTransactionFile(string file)
    {
        return file.EndsWith("-wal", StringComparison.OrdinalIgnoreCase) || file.EndsWith("-shm", StringComparison.OrdinalIgnoreCase);
    }

    // Moves a legacy record into the machine root, or drops it when the machine root holds one of that name.
    private static void MoveOrDrop(string source, string target)
    {
        if (File.Exists(target))
        {
            File.Delete(source);
            return;
        }

        File.Move(source, target);
    }

    private static bool PathsEqual(string a, string b)
    {
        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            StringComparison.OrdinalIgnoreCase);
    }
}
