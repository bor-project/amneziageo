using AmneziaGeo.Decl;
using AmneziaGeo.Geo;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Manages tunnel configurations stored in the state database.
/// </summary>
internal sealed class ConfigRepository(IStateStore store, ServiceManager serviceManager)
{
    /// <summary>
    /// Returns whether a configuration with the given name is stored.
    /// </summary>
    public Task<bool> ExistsAsync(string name, CancellationToken ct = default)
    {
        return store.ConfigExistsAsync(name, ct);
    }

    /// <summary>
    /// Returns the names of all stored configurations.
    /// </summary>
    public Task<IReadOnlyList<string>> ListAsync(CancellationToken ct = default)
    {
        return store.ListConfigNamesAsync(ct);
    }

    /// <summary>
    /// Stores the order the configurations are listed in.
    /// </summary>
    public Task ReorderAsync(IReadOnlyList<string> names, CancellationToken ct = default)
    {
        return store.SetConfigOrderAsync(names, ct);
    }

    /// <summary>
    /// Imports a new configuration from a wg-quick file.
    /// </summary>
    public async Task AddAsync(string name, string sourcePath, CancellationToken ct = default)
    {
        EnsureValidName(name);
        if (await store.ConfigExistsAsync(name, ct))
        {
            throw new InvalidOperationException($"configuration {name} already exists");
        }

        var text = await File.ReadAllTextAsync(sourcePath, ct);
        EnsureValidConfig(text);
        await store.SaveConfigAsync(name, text, ct);
        await SeedIpv6DefaultAsync(name, text, ct);
    }

    /// <summary>
    /// Returns the stored wg-quick text of a configuration (for export / connect).
    /// </summary>
    public async Task<string> ReadTextAsync(string name, CancellationToken ct = default)
    {
        return await store.GetConfigTextAsync(name, ct)
            ?? throw new InvalidOperationException($"configuration {name} does not exist");
    }

    /// <summary>
    /// Imports a new configuration from raw wg-quick text.
    /// </summary>
    public async Task AddFromTextAsync(string name, string text, CancellationToken ct = default)
    {
        EnsureValidName(name);
        if (await store.ConfigExistsAsync(name, ct))
        {
            throw new InvalidOperationException($"configuration {name} already exists");
        }

        EnsureValidConfig(text);
        await store.SaveConfigAsync(name, text, ct);
        await SeedIpv6DefaultAsync(name, text, ct);
    }

    /// <summary>
    /// Replaces the wg-quick text of an existing configuration from a file.
    /// </summary>
    public async Task EditAsync(string name, string sourcePath, CancellationToken ct = default)
    {
        if (!await store.ConfigExistsAsync(name, ct))
        {
            throw new InvalidOperationException($"configuration {name} does not exist");
        }

        var text = await File.ReadAllTextAsync(sourcePath, ct);
        EnsureValidConfig(text);
        await store.SaveConfigAsync(name, text, ct);
    }

    /// <summary>
    /// Overwrites an existing configuration's wg-quick text in place.
    /// </summary>
    public async Task EditFromTextAsync(string name, string text, CancellationToken ct = default)
    {
        if (!await store.ConfigExistsAsync(name, ct))
        {
            throw new InvalidOperationException($"configuration {name} does not exist");
        }

        EnsureValidConfig(text);
        await store.SaveConfigAsync(name, text, ct);
    }

    /// <summary>
    /// Duplicates a configuration together with its geo settings and saved resolutions.
    /// </summary>
    public async Task CopyAsync(string source, string destination, CancellationToken ct = default)
    {
        EnsureValidName(destination);
        var text = await store.GetConfigTextAsync(source, ct)
            ?? throw new InvalidOperationException($"configuration {source} does not exist");

        if (await store.ConfigExistsAsync(destination, ct))
        {
            throw new InvalidOperationException($"configuration {destination} already exists");
        }

        await store.SaveConfigAsync(destination, text, ct);

        var geo = await store.GetTunnelGeoAsync(source, ct);
        if (geo is not null)
        {
            await store.SaveTunnelGeoAsync(geo with { Name = destination }, ct);
        }

        foreach (var resolution in await store.ListDomainResolutionsAsync(source, ct))
        {
            await store.SaveDomainResolutionAsync(destination, resolution, 0, ct);
        }
    }

    /// <summary>
    /// Renames a configuration and carries its associated state to the new name.
    /// </summary>
    public async Task RenameAsync(string oldName, string newName, CancellationToken ct = default)
    {
        EnsureValidName(newName);
        if (string.Equals(oldName, newName, StringComparison.Ordinal))
        {
            return;
        }

        if (!await store.ConfigExistsAsync(oldName, ct))
        {
            throw new InvalidOperationException($"configuration {oldName} does not exist");
        }

        if (await store.ConfigExistsAsync(newName, ct))
        {
            throw new InvalidOperationException($"configuration {newName} already exists");
        }

        await store.RenameConfigAsync(oldName, newName, ct);
        await ConfigRename.CarryAsync(store, oldName, newName, ct);
    }

    /// <summary>
    /// Deletes a configuration, its service, geo settings, and resolutions.
    /// </summary>
    public async Task RemoveAsync(string name, CancellationToken ct = default)
    {
        if (serviceManager.Exists(name))
        {
            serviceManager.Uninstall(name);
        }

        await store.RemoveConfigAsync(name, ct);
        await store.RemoveTunnelGeoAsync(name, ct);
        await store.RemoveConfigTransportAsync(name, ct);
        await store.RemoveConfigDnsAsync(name, ct);
        await store.RemoveConfigExclusionsAsync(name, ct);
        await store.RemoveConfigRoutingAsync(name, ct);
        await store.RemoveDomainResolutionsAsync(name, ct);

        RemoveLegacyConfigFile(name);
    }

    // Removes the config file from disk; migration would otherwise resurrect a deleted config on the next start.
    private static void RemoveLegacyConfigFile(string name)
    {
        var path = Path.Combine(TunnelPaths.ConfigurationsDirectory(), name + ".conf");
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// Imports legacy on-disk wg-quick files into the state database.
    /// </summary>
    public async Task MigrateLegacyConfigsAsync(CancellationToken ct = default)
    {
        var directory = TunnelPaths.ConfigurationsDirectory();
        RenameLegacyDirectory(directory);
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(directory, "*.conf"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrEmpty(name) || await store.ConfigExistsAsync(name, ct))
            {
                continue;
            }

            try
            {
                var text = await File.ReadAllTextAsync(path, ct);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    await store.SaveConfigAsync(name, text, ct);
                }
            }
            catch (IOException)
            {
                // Skip an unreadable legacy file; the rest still migrate.
            }
        }
    }

    // Renames the pre-rename "Configurations" folder to the current "config" folder when the new one is absent.
    private static void RenameLegacyDirectory(string current)
    {
        var parent = Path.GetDirectoryName(current);
        if (parent is null)
        {
            return;
        }

        var legacy = Path.Combine(parent, "Configurations");
        if (Directory.Exists(current) || !Directory.Exists(legacy))
        {
            return;
        }

        try
        {
            Directory.Move(legacy, current);
        }
        catch (IOException)
        {
        }
    }

    private static void EnsureValidName(string name)
    {
        // Name doubles as the Windows service name, so keep it filesystem-safe.
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException($"invalid configuration name: {name}");
        }
    }

    private static void EnsureValidConfig(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException("not a valid WireGuard/AmneziaWG configuration");
        }

        WgConfigValidator.Validate(text);
    }

    // Defaults IPv6 on when the imported config has an IPv6 interface Address.
    private async Task SeedIpv6DefaultAsync(string name, string text, CancellationToken ct)
    {
        if (WgConfigEditor.GetAddresses(text).Any(a => a.Contains(':')))
        {
            await store.SetConfigTransportAsync(new ConfigTransport(name, false, string.Empty, 443, UseIpv6: true), ct);
        }
    }
}
