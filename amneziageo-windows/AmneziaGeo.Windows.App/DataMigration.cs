using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Migrates the legacy machine-wide library into the per-user data root.
/// </summary>
internal static class DataMigration
{
    private const string StateDb = "state.db";
    private const string RetiredDb = "state.db.legacy";

    private static readonly string[] DbSuffixes = ["", "-wal", "-shm"];

    /// <summary>
    /// Copies the legacy machine-wide library into an empty per-user root, then retires the legacy file.
    /// </summary>
    public static void SeedFromProgramData(ILogger logger)
    {
        var target = AppDataRoot.Base();
        if (AppDataRoot.IsMachineRoot(target))
        {
            return;
        }

        var source = AppDataRoot.MachineBase();
        var legacy = Path.Combine(source, StateDb);
        if (!File.Exists(legacy))
        {
            return;
        }

        try
        {
            if (!File.Exists(Path.Combine(target, StateDb)))
            {
                Directory.CreateDirectory(target);
                foreach (var suffix in DbSuffixes)
                {
                    if (File.Exists(legacy + suffix))
                    {
                        File.Copy(legacy + suffix, Path.Combine(target, StateDb + suffix));
                    }
                }

                logger.LogInformation("configurations from the older shared storage {Source} were copied into {Target}, so this user keeps them", source, target);
            }

            // Retire the legacy file: the machine root carries shared assets only, and a library left there is picked
            // up as-is by any root that resolves to it and seeds every profile that logs in later.
            File.Move(legacy, Path.Combine(source, RetiredDb), overwrite: true);
            File.Delete(legacy + "-wal");
            File.Delete(legacy + "-shm");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "configurations could not be moved from the older storage {Source} to {Target}; they stay where they are and the move is tried again next start", source, target);
        }
    }
}
