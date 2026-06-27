using AmneziaGeo.Decl;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Manages stored tunnel configurations and their associated geo and resolution state. The wg-quick text
/// of every configuration lives in the state database (the configs table) - the single place all
/// configuration is kept, so a backup is just a copy of the database. There are no on-disk .conf files;
/// the tunnel service reads a config's text straight from the database when it connects.
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
    /// Imports a new configuration from raw wg-quick text (parsed UI-side from a file, link, or QR).
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
    /// Overwrites an existing configuration's wg-quick text in place (manual edit). The config's profile
    /// memberships, geo, and routing state are preserved - only its text changes.
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
            await store.SaveDomainResolutionAsync(destination, resolution, ct);
        }
    }

    /// <summary>
    /// Renames a configuration: moves its stored text and carries its geo settings, transport settings,
    /// saved resolutions, and balancer memberships over to the new name. The destination must be free.
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

        var geo = await store.GetTunnelGeoAsync(oldName, ct);
        if (geo is not null)
        {
            await store.SaveTunnelGeoAsync(geo with { Name = newName }, ct);
            await store.RemoveTunnelGeoAsync(oldName, ct);
        }

        var transport = await store.GetConfigTransportAsync(oldName, ct);
        if (transport is not null)
        {
            await store.SetConfigTransportAsync(transport with { Name = newName }, ct);
            await store.RemoveConfigTransportAsync(oldName, ct);
        }

        var dns = await store.GetConfigDnsAsync(oldName, ct);
        if (dns is not null)
        {
            await store.SetConfigDnsAsync(dns with { Name = newName }, ct);
            await store.RemoveConfigDnsAsync(oldName, ct);
        }

        var exclusions = await store.GetConfigExclusionsAsync(oldName, ct);
        if (exclusions is not null)
        {
            await store.SetConfigExclusionsAsync(exclusions with { Name = newName }, ct);
            await store.RemoveConfigExclusionsAsync(oldName, ct);
        }

        foreach (var resolution in await store.ListDomainResolutionsAsync(oldName, ct))
        {
            await store.SaveDomainResolutionAsync(newName, resolution, ct);
        }

        await store.RemoveDomainResolutionsAsync(oldName, ct);

        foreach (var profileName in await store.ListBalancerNamesAsync(ct))
        {
            var profile = await store.GetBalancerAsync(profileName, ct);
            if (profile is null || !string.Equals(profile.Config, oldName, StringComparison.Ordinal))
            {
                continue;
            }

            await store.SaveBalancerAsync(profile with { Config = newName }, ct);
        }
    }

    /// <summary>
    /// Deletes a configuration, its service, geo settings, resolutions, and balancer memberships.
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
        await store.RemoveDomainResolutionsAsync(name, ct);

        foreach (var profileName in await store.ListBalancerNamesAsync(ct))
        {
            var profile = await store.GetBalancerAsync(profileName, ct);
            if (profile is null || !string.Equals(profile.Config, name, StringComparison.Ordinal))
            {
                continue;
            }

            await store.SaveBalancerAsync(profile with { Config = string.Empty }, ct);
        }
    }

    /// <summary>
    /// One-time migration: imports any legacy on-disk wg-quick files (Configurations\*.conf, from before
    /// configs lived in the database) into the state database. The files are left in place as a safety
    /// copy but are no longer read. Idempotent - a config already in the database is left untouched, so a
    /// later edit in the database is never clobbered by the stale file.
    /// </summary>
    public async Task MigrateLegacyConfigsAsync(CancellationToken ct = default)
    {
        var directory = TunnelPaths.ConfigurationsDirectory();
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

    private static void EnsureValidName(string name)
    {
        // The name is the config's identity and is also used as the Windows tunnel service name, so keep
        // the conservative filesystem-safe character guard even though configs are no longer files.
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException($"invalid configuration name: {name}");
        }
    }

    private static void EnsureValidConfig(string text)
    {
        if (string.IsNullOrWhiteSpace(text)
            || !text.Contains("[Interface]", StringComparison.OrdinalIgnoreCase)
            || !text.Contains("[Peer]", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("not a valid WireGuard/AmneziaWG configuration");
        }
    }
}
