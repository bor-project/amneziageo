using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>The result of a seed-apply attempt (also used by tests).</summary>
internal enum SeedOutcome
{
    /// <summary>No <c>state.db.seed</c> was laid - this build bundles no default DB.</summary>
    NoSeed,

    /// <summary>This exact seed + policy was already applied (marker matched) - nothing done.</summary>
    AlreadyApplied,

    /// <summary>No state.db existed, so the seed was deployed as the new database.</summary>
    Seeded,

    /// <summary>A state.db existed and the replace flag was present, so it was overwritten.</summary>
    Replaced,

    /// <summary>A state.db existed and no replace flag, so the existing database was kept.</summary>
    Kept,

    /// <summary>Something went wrong; the existing/empty store is used unchanged.</summary>
    Failed,
}

/// <summary>
/// Deploys a bundled "default configuration" database laid next to the agent by the installer (#54).
/// The MSI drops <c>state.db.seed</c> into the install directory when installer.config.json sets a
/// <c>defaultConfigDb</c>; the agent (running as SYSTEM on first start) copies it into the shared data
/// directory as <c>state.db</c> before the store is opened, so a fresh install comes up preconfigured.
///
/// Conflict policy when a <c>state.db</c> already exists at the destination (an upgrade over existing
/// data): replace it only when the installer asked to - i.e. when <c>state.db.seed.replace</c> was laid
/// (the replace-on-conflict choice: the bundle REPLACEDB parameter or the BA's replace/skip dialog).
/// Otherwise the existing database is kept (skip). A <b>missing</b> <c>state.db</c> is <b>always</b>
/// (re)seeded, regardless of the marker, so a fresh machine - even one carrying a stale marker left in
/// ProgramData by a previous install - still comes up with the bundled default.
///
/// Idempotency is keyed on the seed's <b>content</b> (SHA-256) plus the replace decision, recorded in a
/// marker next to state.db. A restart with the same seed and the same policy does nothing; a changed seed
/// OR a skip-&gt;replace transition yields a different signature and re-applies. Identity is deliberately NOT
/// the installed seed's mtime: Windows Installer restores the source file's build-time timestamp, so the
/// laid seed's mtime is not "fresh per install" and cannot distinguish one install from the next.
///
/// Best-effort: any failure is logged and swallowed so a bad seed never blocks agent startup - the agent
/// then just opens the existing or a fresh empty store. Should run only on the SYSTEM agent path.
/// </summary>
internal static class SeedImporter
{
    public static SeedOutcome TryApply(ILogger log) =>
        Apply(ResolveSeedSource(), TunnelPaths.StateDbFile(), TunnelPaths.SeedReplaceFlagFile(), log);

    /// <summary>
    /// The seed database to deploy. A user-selected file recorded by the installer (HKLM\Software\AmneziaGeo
    /// value SeedSource - the BA file-picker or the SEEDDBPATH command-line argument, #55) takes priority over
    /// the bundled <c>state.db.seed</c> (#54). Falls back to the bundled path; Apply() no-ops if neither file
    /// exists. Best-effort: a missing key / unreadable registry just uses the bundled seed.
    /// </summary>
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
            // fall through to the bundled seed
        }

        return TunnelPaths.SeedDbFile();
    }

    /// <summary>
    /// Testable core: applies <paramref name="seed"/> into <paramref name="db"/> using the explicit paths,
    /// with no static path resolution. <paramref name="replaceFlag"/> present => overwrite an existing DB.
    /// </summary>
    internal static SeedOutcome Apply(string seed, string db, string replaceFlag, ILogger log)
    {
        try
        {
            if (!File.Exists(seed))
            {
                return SeedOutcome.NoSeed; // no default DB bundled with this build
            }

            var dir = Path.GetDirectoryName(db)!;
            Directory.CreateDirectory(dir);

            var replace = File.Exists(replaceFlag);
            // Identity = seed CONTENT hash + the replace decision. Content (not mtime) so a re-laid seed
            // with the installer-restored build timestamp is still recognised, and a genuinely different
            // default re-applies; the :R/:K suffix makes a skip->replace on the same build re-apply.
            var signature = HashFile(seed) + (replace ? ":R" : ":K");
            var marker = Path.Combine(dir, "seed-applied.marker");

            var dbExists = File.Exists(db);

            // A present DB short-circuits only when THIS exact (seed, policy) was already applied. A missing
            // DB always (re)seeds: the marker must never block the fresh-machine invariant (a stale marker
            // can survive in ProgramData across an uninstall, since the MSI does not manage that directory).
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

    /// <summary>
    /// Never lets the store observe a half-written file: copies to a sibling temp then atomically replaces,
    /// so a crash mid-copy leaves either the old DB or the complete new one, never a truncated state.db.
    /// </summary>
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
