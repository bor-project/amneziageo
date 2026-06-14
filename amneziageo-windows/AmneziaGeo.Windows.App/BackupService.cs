using System.IO.Compression;
using System.Text.Json;
using AmneziaGeo.Decl;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Exports and restores the full configuration (state database and tunnel configs) as a portable zip archive.
/// </summary>
internal sealed class BackupService(IStateStore store, ServiceManager serviceManager, ILogger<BackupService> logger)
{
    private const string _format = "amneziageo-backup";

    /// <summary>
    /// Writes a backup archive containing a consistent state-db snapshot, every config, and a manifest.
    /// </summary>
    public async Task<int> BackupAsync(string path)
    {
        var snapshot = Path.Combine(Path.GetTempPath(), $"ageo-snapshot-{Guid.NewGuid():N}.db");
        try
        {
            await store.BackupToAsync(snapshot);

            var configs = ConfigNames();
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(snapshot, "state.db", CompressionLevel.Optimal);
                foreach (var name in configs)
                {
                    zip.CreateEntryFromFile(TunnelPaths.ConfigFile(name), $"Configurations/{name}.conf", CompressionLevel.Optimal);
                }

                var manifest = new BackupManifest(_format, 1, DateTimeOffset.UtcNow, AppVersion(), true, configs);
                var entry = zip.CreateEntry("manifest.json", CompressionLevel.Optimal);
                using (var stream = entry.Open())
                {
                    await JsonSerializer.SerializeAsync(stream, manifest);
                }
            }

            logger.LogInformation("backup written to {Path} ({Count} configs)", path, configs.Count);
            Console.WriteLine($"backup written: {path}");
            Console.WriteLine($"  {configs.Count} config(s) + state.db");
            Console.WriteLine("WARNING: this archive contains real private keys (in the .conf files and the database). Store and transfer it securely.");
            return 0;
        }
        finally
        {
            if (File.Exists(snapshot))
            {
                File.Delete(snapshot);
            }
        }
    }

    /// <summary>
    /// Replaces the local state database and configs from a backup archive, after a running-agent and confirmation gate.
    /// </summary>
    public async Task<int> RestoreAsync(string path, bool force)
    {
        if (!File.Exists(path))
        {
            Console.WriteLine($"archive not found: {path}");
            return 1;
        }

        using (var zip = ZipFile.OpenRead(path))
        {
            var manifestEntry = zip.GetEntry("manifest.json");
            if (manifestEntry is null)
            {
                Console.WriteLine("not a valid AmneziaGeo backup (manifest.json missing)");
                return 1;
            }

            BackupManifest? manifest;
            using (var stream = manifestEntry.Open())
            {
                manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(stream);
            }

            if (manifest is null || manifest.Format != _format)
            {
                Console.WriteLine("not a valid AmneziaGeo backup (bad manifest)");
                return 1;
            }

            if (serviceManager.AgentState() == "RUNNING")
            {
                Console.WriteLine("agent is running; stop it first (agent-stop / agent-uninstall) before restoring");
                return 1;
            }

            Console.WriteLine($"restore from {path} (created {manifest.CreatedUtc:u})");
            Console.WriteLine($"  configs: {string.Join(", ", manifest.Configs)}");
            if (!force)
            {
                Console.Write("this REPLACES the current setup. continue? [y/N] ");
                var answer = Console.ReadLine();
                if (!string.Equals(answer?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("aborted");
                    return 1;
                }
            }

            var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss");
            var dbPath = TunnelPaths.StateDbFile();
            var configsDir = TunnelPaths.ConfigurationsDirectory();

            store.ClearPool();

            var restored = 0;
            try
            {
                if (File.Exists(dbPath))
                {
                    File.Move(dbPath, $"{dbPath}.pre-restore-{stamp}");
                }

                if (Directory.Exists(configsDir))
                {
                    Directory.Move(configsDir, $"{configsDir}.pre-restore-{stamp}");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
                zip.GetEntry("state.db")?.ExtractToFile(dbPath, overwrite: true);

                Directory.CreateDirectory(configsDir);
                foreach (var entry in zip.Entries)
                {
                    if (!entry.FullName.StartsWith("Configurations/", StringComparison.Ordinal) || entry.FullName.EndsWith('/'))
                    {
                        continue;
                    }

                    var fileName = Path.GetFileName(entry.FullName);
                    if (string.IsNullOrEmpty(fileName) || !fileName.EndsWith(".conf", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    entry.ExtractToFile(Path.Combine(configsDir, fileName), overwrite: true);
                    restored++;
                }
            }
            catch (IOException ex)
            {
                logger.LogError(ex, "restore failed during file replacement");
                Console.WriteLine($"restore failed: {ex.Message} - is a tunnel or the agent still running?");
                return 1;
            }

            logger.LogInformation("restore complete: {Count} configs from {Path}", restored, path);
            Console.WriteLine($"restored {restored} config(s) + state.db");
            if (File.Exists($"{dbPath}.pre-restore-{stamp}"))
            {
                Console.WriteLine($"previous state.db kept as {dbPath}.pre-restore-{stamp}");
            }

            Console.WriteLine("next: run 'update-sources' to re-download geo data, then 'agent-install <target>' to recreate the agent");
            return 0;
        }
    }

    private static List<string> ConfigNames()
    {
        var configs = new List<string>();
        var directory = TunnelPaths.ConfigurationsDirectory();
        if (Directory.Exists(directory))
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.conf"))
            {
                configs.Add(Path.GetFileNameWithoutExtension(file));
            }
        }

        configs.Sort(StringComparer.Ordinal);
        return configs;
    }

    private static string AppVersion()
    {
        return typeof(BackupService).Assembly.GetName().Version?.ToString() ?? "0";
    }
}
