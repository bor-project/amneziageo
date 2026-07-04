using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Seed-apply result.
/// </summary>
internal enum SeedOutcome
{
    /// <summary>
    /// No seed bundled.
    /// </summary>
    NoSeed,

    /// <summary>
    /// Same seed and policy already applied.
    /// </summary>
    AlreadyApplied,

    /// <summary>
    /// Seed deployed as new database.
    /// </summary>
    Seeded,

    /// <summary>
    /// Existing database overwritten.
    /// </summary>
    Replaced,

    /// <summary>
    /// Existing database kept.
    /// </summary>
    Kept,

    /// <summary>
    /// Apply failed; store unchanged.
    /// </summary>
    Failed,
}

/// <summary>
/// Deploys the bundled default configuration database.
/// </summary>
internal static class SeedImporter
{
    public static SeedOutcome TryApply(ILogger log) =>
        Apply(ResolveSeedSource(), TunnelPaths.StateDbFile(), TunnelPaths.SeedReplaceFlagFile(), log);

    private static string ResolveSeedSource()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"Software\AmneziaGeo");
            if (key?.GetValue("SeedSource") is string picked)
            {
                var path = picked.Trim();
                if (path.Length > 0 && File.Exists(path))
                {
                    return path;
                }
            }
        }
        catch
        {
        }

        return TunnelPaths.SeedDbFile();
    }

    /// <summary>
    /// Testable core with explicit paths.
    /// </summary>
    internal static SeedOutcome Apply(string seed, string db, string replaceFlag, ILogger log)
    {
        try
        {
            if (!File.Exists(seed))
            {
                return SeedOutcome.NoSeed;
            }

            var dir = Path.GetDirectoryName(db)!;
            Directory.CreateDirectory(dir);

            var replace = File.Exists(replaceFlag);
            var signature = HashFile(seed) + (replace ? ":R" : ":K");
            var marker = Path.Combine(dir, "seed-applied.marker");

            var dbExists = File.Exists(db);

            if (dbExists &&
                File.Exists(marker) &&
                string.Equals(SafeReadAllText(marker), signature, StringComparison.Ordinal))
            {
                return SeedOutcome.AlreadyApplied;
            }

            SeedOutcome outcome;
            if (!dbExists)
            {
                CopyAtomic(seed, db);
                outcome = SeedOutcome.Seeded;
                log.LogInformation("Default configuration database seeded from {Seed}.", seed);
            }
            else if (replace)
            {
                CopyAtomic(seed, db);
                outcome = SeedOutcome.Replaced;
                log.LogInformation("Existing configuration database replaced with the installer default from {Seed}.", seed);
            }
            else
            {
                outcome = SeedOutcome.Kept;
                log.LogInformation("Default configuration database available, but an existing state.db was kept (skip).");
            }

            File.WriteAllText(marker, signature);
            return outcome;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Applying the default configuration database failed; continuing with the existing or empty store.");
            return SeedOutcome.Failed;
        }
    }

    private static void CopyAtomic(string seed, string db)
    {
        var tmp = db + ".seedtmp";
        File.Copy(seed, tmp, overwrite: true);
        File.Move(tmp, db, overwrite: true);
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string SafeReadAllText(string path)
    {
        try
        {
            return File.ReadAllText(path).Trim();
        }
        catch
        {
            return string.Empty;
        }
    }
}
