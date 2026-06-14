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
        await store.RemoveDomainResolutionsAsync(name, ct);

        foreach (var balancerName in await store.ListBalancerNamesAsync(ct))
        {
            var balancer = await store.GetBalancerAsync(balancerName, ct);
            if (balancer is null || !balancer.Members.Contains(name))
            {
                continue;
            }

            var members = balancer.Members.Where(member => member != name).ToList();
            await store.SaveBalancerAsync(balancer with { Members = members }, ct);
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
