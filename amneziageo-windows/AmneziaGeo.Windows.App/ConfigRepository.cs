using AmneziaGeo.Decl;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Manages stored tunnel configurations and their associated geo and resolution state.
/// </summary>
internal sealed class ConfigRepository(IStateStore store, ServiceManager serviceManager)
{
    /// <summary>
    /// Returns whether a configuration with the given name is stored.
    /// </summary>
    public bool Exists(string name)
    {
        return File.Exists(TunnelPaths.ConfigFile(name));
    }

    /// <summary>
    /// Returns the names of all stored configurations.
    /// </summary>
    public IReadOnlyList<string> List()
    {
        var directory = TunnelPaths.ConfigurationsDirectory();
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var names = new List<string>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.conf"))
        {
            names.Add(Path.GetFileNameWithoutExtension(path));
        }

        names.Sort(StringComparer.Ordinal);
        return names;
    }

    /// <summary>
    /// Imports a new configuration from a wg-quick file.
    /// </summary>
    public void Add(string name, string sourcePath)
    {
        EnsureValidName(name);
        if (Exists(name))
        {
            throw new InvalidOperationException($"configuration {name} already exists");
        }

        var stored = TunnelPaths.ConfigFile(name);
        Directory.CreateDirectory(Path.GetDirectoryName(stored)!);
        File.Copy(sourcePath, stored, overwrite: false);
    }

    /// <summary>
    /// Returns the stored wg-quick text of a configuration (for export).
    /// </summary>
    public string ReadText(string name)
    {
        if (!Exists(name))
        {
            throw new InvalidOperationException($"configuration {name} does not exist");
        }

        return File.ReadAllText(TunnelPaths.ConfigFile(name));
    }

    /// <summary>
    /// Imports a new configuration from raw wg-quick text (parsed UI-side from a file, link, or QR).
    /// </summary>
    public void AddFromText(string name, string text)
    {
        EnsureValidName(name);
        if (Exists(name))
        {
            throw new InvalidOperationException($"configuration {name} already exists");
        }

        if (string.IsNullOrWhiteSpace(text)
            || !text.Contains("[Interface]", StringComparison.OrdinalIgnoreCase)
            || !text.Contains("[Peer]", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("not a valid WireGuard/AmneziaWG configuration");
        }

        var stored = TunnelPaths.ConfigFile(name);
        Directory.CreateDirectory(Path.GetDirectoryName(stored)!);
        File.WriteAllText(stored, text);
    }

    /// <summary>
    /// Replaces the wg-quick text of an existing configuration.
    /// </summary>
    public void Edit(string name, string sourcePath)
    {
        if (!Exists(name))
        {
            throw new InvalidOperationException($"configuration {name} does not exist");
        }

        File.Copy(sourcePath, TunnelPaths.ConfigFile(name), overwrite: true);
    }

    /// <summary>
    /// Overwrites an existing configuration's wg-quick text in place (manual edit). The file (and thus the
    /// config's profile memberships, geo, and routing state) is preserved - only its contents change.
    /// </summary>
    public void EditFromText(string name, string text)
    {
        if (!Exists(name))
        {
            throw new InvalidOperationException($"configuration {name} does not exist");
        }

        if (string.IsNullOrWhiteSpace(text)
            || !text.Contains("[Interface]", StringComparison.OrdinalIgnoreCase)
            || !text.Contains("[Peer]", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("not a valid WireGuard/AmneziaWG configuration");
        }

        File.WriteAllText(TunnelPaths.ConfigFile(name), text);
    }

    /// <summary>
    /// Duplicates a configuration together with its geo settings and saved resolutions.
    /// </summary>
    public async Task CopyAsync(string source, string destination, CancellationToken ct = default)
    {
        EnsureValidName(destination);
        if (!Exists(source))
        {
            throw new InvalidOperationException($"configuration {source} does not exist");
        }

        if (Exists(destination))
        {
            throw new InvalidOperationException($"configuration {destination} already exists");
        }

        File.Copy(TunnelPaths.ConfigFile(source), TunnelPaths.ConfigFile(destination), overwrite: false);

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
    /// Renames a configuration: moves the .conf and carries its geo settings, transport settings, saved
    /// resolutions, and balancer memberships over to the new name. The destination must be free.
    /// </summary>
    public async Task RenameAsync(string oldName, string newName, CancellationToken ct = default)
    {
        EnsureValidName(newName);
        if (string.Equals(oldName, newName, StringComparison.Ordinal))
        {
            return;
        }

        if (!Exists(oldName))
        {
            throw new InvalidOperationException($"configuration {oldName} does not exist");
        }

        if (Exists(newName))
        {
            throw new InvalidOperationException($"configuration {newName} already exists");
        }

        File.Move(TunnelPaths.ConfigFile(oldName), TunnelPaths.ConfigFile(newName));

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

        var stored = TunnelPaths.ConfigFile(name);
        if (File.Exists(stored))
        {
            File.Delete(stored);
        }

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

    private static void EnsureValidName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException($"invalid configuration name: {name}");
        }
    }
}
