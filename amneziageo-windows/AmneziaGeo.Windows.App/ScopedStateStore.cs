using AmneziaGeo.Decl;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Composite state store: routes shared geo assets and machine settings to the machine store, and the user
/// library (configs, routing, projections, resolutions, per-user settings) to a per-user store.
/// </summary>
internal sealed class ScopedStateStore(IStateStore machine, IStateStore user) : IStateStore
{
    // Settings keys owned by the machine store: the tunable app settings plus the internal geo counters.
    private static readonly HashSet<string> MachineSettingKeys = BuildMachineSettingKeys();

    private static HashSet<string> BuildMachineSettingKeys()
    {
        var keys = new HashSet<string>(SettingsStore.Keys(), StringComparer.Ordinal)
        {
            "geo-last-refresh",
            "geo-resolve-epoch",
            "last-owner-root",
            "last-owner-target",
        };
        return keys;
    }

    /// <summary>
    /// Returns whether a settings key belongs to the machine store.
    /// </summary>
    public static bool IsMachineSettingKey(string key)
    {
        return MachineSettingKeys.Contains(key);
    }

    /// <summary>
    /// The settings keys owned by the machine store.
    /// </summary>
    public static IReadOnlyCollection<string> MachineKeys => MachineSettingKeys;

    /// <inheritdoc/>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await machine.InitializeAsync(ct);
        await user.InitializeAsync(ct);
    }

    // Machine store: shared geo download sources and downloaded file metadata.
    /// <inheritdoc/>
    public Task SaveGeoSourceAsync(GeoSource source, CancellationToken ct = default) => machine.SaveGeoSourceAsync(source, ct);

    /// <inheritdoc/>
    public Task<IReadOnlyList<GeoSource>> ListGeoSourcesAsync(CancellationToken ct = default) => machine.ListGeoSourcesAsync(ct);

    /// <inheritdoc/>
    public Task RemoveGeoSourceAsync(string name, CancellationToken ct = default) => machine.RemoveGeoSourceAsync(name, ct);

    // Пользовательская библиотека: подписки идут за конфигурациями.
    /// <inheritdoc/>
    public Task<IReadOnlyList<Subscription>> ListSubscriptionsAsync(CancellationToken ct = default) => user.ListSubscriptionsAsync(ct);

    /// <inheritdoc/>
    public Task SaveSubscriptionAsync(Subscription subscription, CancellationToken ct = default) => user.SaveSubscriptionAsync(subscription, ct);

    /// <inheritdoc/>
    public Task RemoveSubscriptionAsync(string name, CancellationToken ct = default) => user.RemoveSubscriptionAsync(name, ct);

    /// <inheritdoc/>
    public Task<IReadOnlyList<SubscriptionMember>> ListSubscriptionMembersAsync(string? subscription = null, CancellationToken ct = default) => user.ListSubscriptionMembersAsync(subscription, ct);

    /// <inheritdoc/>
    public Task SaveSubscriptionMemberAsync(SubscriptionMember member, CancellationToken ct = default) => user.SaveSubscriptionMemberAsync(member, ct);

    /// <inheritdoc/>
    public Task RemoveSubscriptionMemberAsync(string subscription, string remark, CancellationToken ct = default) => user.RemoveSubscriptionMemberAsync(subscription, remark, ct);

    /// <inheritdoc/>
    public Task RenameSubscriptionMemberAsync(string configName, string newConfigName, CancellationToken ct = default) => user.RenameSubscriptionMemberAsync(configName, newConfigName, ct);

    /// <inheritdoc/>
    public Task<GeoFileMetadata?> GetGeoFileAsync(string name, CancellationToken ct = default) => machine.GetGeoFileAsync(name, ct);

    /// <inheritdoc/>
    public Task SaveGeoFileAsync(GeoFileMetadata metadata, CancellationToken ct = default) => machine.SaveGeoFileAsync(metadata, ct);

    /// <inheritdoc/>
    public Task<IReadOnlyList<GeoFileMetadata>> ListGeoFilesAsync(CancellationToken ct = default) => machine.ListGeoFilesAsync(ct);

    /// <inheritdoc/>
    public Task SetGeoUpdateAvailableAsync(string name, bool available, CancellationToken ct = default) => machine.SetGeoUpdateAvailableAsync(name, available, ct);

    // Settings: routed per key between the machine and user stores.
    /// <inheritdoc/>
    public Task<string?> GetSettingAsync(string key, CancellationToken ct = default)
    {
        return MachineSettingKeys.Contains(key) ? machine.GetSettingAsync(key, ct) : user.GetSettingAsync(key, ct);
    }

    /// <inheritdoc/>
    public Task SetSettingAsync(string key, string value, CancellationToken ct = default)
    {
        return MachineSettingKeys.Contains(key) ? machine.SetSettingAsync(key, value, ct) : user.SetSettingAsync(key, value, ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<string, string>> GetSettingsAsync(CancellationToken ct = default)
    {
        var userSettings = await user.GetSettingsAsync(ct);
        var machineSettings = await machine.GetSettingsAsync(ct);
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in userSettings)
        {
            if (!MachineSettingKeys.Contains(pair.Key))
            {
                merged[pair.Key] = pair.Value;
            }
        }

        foreach (var pair in machineSettings)
        {
            if (MachineSettingKeys.Contains(pair.Key))
            {
                merged[pair.Key] = pair.Value;
            }
        }

        return merged;
    }

    // User store: the per-user library and its runtime state.
    /// <inheritdoc/>
    public Task<TunnelGeo?> GetTunnelGeoAsync(string name, CancellationToken ct = default) => user.GetTunnelGeoAsync(name, ct);

    /// <inheritdoc/>
    public Task<TunnelGeo?> GetActiveTunnelGeoAsync(string name, CancellationToken ct = default) => user.GetActiveTunnelGeoAsync(name, ct);

    /// <inheritdoc/>
    public Task SaveTunnelGeoAsync(TunnelGeo geo, CancellationToken ct = default) => user.SaveTunnelGeoAsync(geo, ct);

    /// <inheritdoc/>
    public Task SaveTunnelProjectionAsync(string name, bool split, IReadOnlyList<string> routes, IReadOnlyList<GeoDomain> domains, IReadOnlyList<string> apps, IReadOnlyList<string> directRoutes, IReadOnlyList<GeoDomain> directDomains, IReadOnlyList<string> blockRoutes, IReadOnlyList<GeoDomain> blockDomains, long? routingListId, CancellationToken ct = default)
        => user.SaveTunnelProjectionAsync(name, split, routes, domains, apps, directRoutes, directDomains, blockRoutes, blockDomains, routingListId, ct);

    /// <inheritdoc/>
    public Task ClearTunnelProjectionAsync(string name, CancellationToken ct = default) => user.ClearTunnelProjectionAsync(name, ct);

    /// <inheritdoc/>
    public Task<long?> GetActiveRoutingListIdAsync(string name, CancellationToken ct = default) => user.GetActiveRoutingListIdAsync(name, ct);

    /// <inheritdoc/>
    public Task<IReadOnlyList<string>> ListTunnelGeoNamesAsync(CancellationToken ct = default) => user.ListTunnelGeoNamesAsync(ct);

    /// <inheritdoc/>
    public Task RemoveTunnelGeoAsync(string name, CancellationToken ct = default) => user.RemoveTunnelGeoAsync(name, ct);

    /// <inheritdoc/>
    public Task<ConfigTransport?> GetConfigTransportAsync(string name, CancellationToken ct = default) => user.GetConfigTransportAsync(name, ct);

    /// <inheritdoc/>
    public Task SetConfigTransportAsync(ConfigTransport transport, CancellationToken ct = default) => user.SetConfigTransportAsync(transport, ct);

    /// <inheritdoc/>
    public Task RemoveConfigTransportAsync(string name, CancellationToken ct = default) => user.RemoveConfigTransportAsync(name, ct);

    /// <inheritdoc/>
    public Task<ConfigDns?> GetConfigDnsAsync(string name, CancellationToken ct = default) => user.GetConfigDnsAsync(name, ct);

    /// <inheritdoc/>
    public Task SetConfigDnsAsync(ConfigDns dns, CancellationToken ct = default) => user.SetConfigDnsAsync(dns, ct);

    /// <inheritdoc/>
    public Task RemoveConfigDnsAsync(string name, CancellationToken ct = default) => user.RemoveConfigDnsAsync(name, ct);

    /// <inheritdoc/>
    public Task<ConfigExclusions?> GetConfigExclusionsAsync(string name, CancellationToken ct = default) => user.GetConfigExclusionsAsync(name, ct);

    /// <inheritdoc/>
    public Task SetConfigExclusionsAsync(ConfigExclusions exclusions, CancellationToken ct = default) => user.SetConfigExclusionsAsync(exclusions, ct);

    /// <inheritdoc/>
    public Task RemoveConfigExclusionsAsync(string name, CancellationToken ct = default) => user.RemoveConfigExclusionsAsync(name, ct);

    /// <inheritdoc/>
    public Task<bool> ConfigExistsAsync(string name, CancellationToken ct = default) => user.ConfigExistsAsync(name, ct);

    /// <inheritdoc/>
    public Task<string?> GetConfigTextAsync(string name, CancellationToken ct = default) => user.GetConfigTextAsync(name, ct);

    /// <inheritdoc/>
    public Task<IReadOnlyList<string>> ListConfigNamesAsync(CancellationToken ct = default) => user.ListConfigNamesAsync(ct);

    /// <inheritdoc/>
    public Task SetConfigOrderAsync(IReadOnlyList<string> names, CancellationToken ct = default) => user.SetConfigOrderAsync(names, ct);

    /// <inheritdoc/>
    public Task SaveConfigAsync(string name, string text, CancellationToken ct = default) => user.SaveConfigAsync(name, text, ct);

    /// <inheritdoc/>
    public Task RenameConfigAsync(string oldName, string newName, CancellationToken ct = default) => user.RenameConfigAsync(oldName, newName, ct);

    /// <inheritdoc/>
    public Task RemoveConfigAsync(string name, CancellationToken ct = default) => user.RemoveConfigAsync(name, ct);

    /// <inheritdoc/>
    public Task SaveDomainResolutionAsync(string tunnel, DomainResolution resolution, long listId, CancellationToken ct = default)
        => user.SaveDomainResolutionAsync(tunnel, resolution, listId, ct);

    /// <inheritdoc/>
    public Task<IReadOnlyList<DomainResolution>> ListDomainResolutionsAsync(string tunnel, CancellationToken ct = default) => user.ListDomainResolutionsAsync(tunnel, ct);

    /// <inheritdoc/>
    public Task RemoveDomainResolutionsAsync(string tunnel, CancellationToken ct = default) => user.RemoveDomainResolutionsAsync(tunnel, ct);

    /// <inheritdoc/>
    public Task DeleteDomainResolutionAsync(string tunnel, string domain, CancellationToken ct = default) => user.DeleteDomainResolutionAsync(tunnel, domain, ct);

    /// <inheritdoc/>
    public Task<DomainResolution?> GetDomainResolutionAsync(string tunnel, string domain, CancellationToken ct = default) => user.GetDomainResolutionAsync(tunnel, domain, ct);

    /// <inheritdoc/>
    public Task<long> SaveRoutingListAsync(RoutingList list, CancellationToken ct = default) => user.SaveRoutingListAsync(list, ct);

    /// <inheritdoc/>
    public Task<RoutingList?> GetRoutingListAsync(long id, CancellationToken ct = default) => user.GetRoutingListAsync(id, ct);

    /// <inheritdoc/>
    public Task<RoutingList?> GetRoutingListByNameAsync(string name, CancellationToken ct = default) => user.GetRoutingListByNameAsync(name, ct);

    /// <inheritdoc/>
    public Task<IReadOnlyList<RoutingList>> ListRoutingListsAsync(CancellationToken ct = default) => user.ListRoutingListsAsync(ct);

    /// <inheritdoc/>
    public Task<IReadOnlyList<RoutingListSummary>> ListRoutingListSummariesAsync(CancellationToken ct = default)
        => user.ListRoutingListSummariesAsync(ct);

    /// <inheritdoc/>
    public Task SetRoutingListOrderAsync(IReadOnlyList<string> names, CancellationToken ct = default) => user.SetRoutingListOrderAsync(names, ct);

    /// <inheritdoc/>
    public Task RemoveRoutingListAsync(long id, CancellationToken ct = default) => user.RemoveRoutingListAsync(id, ct);

    /// <inheritdoc/>
    public Task<ActiveRoutingListMaterialization?> GetActiveRoutingListMaterializationAsync(string tunnel, CancellationToken ct = default)
        => user.GetActiveRoutingListMaterializationAsync(tunnel, ct);

    /// <inheritdoc/>
    public Task<long?> GetActiveRoutingListGenerationAsync(string tunnel, CancellationToken ct = default) => user.GetActiveRoutingListGenerationAsync(tunnel, ct);

    /// <inheritdoc/>
    public Task<RoutingSettings?> GetRoutingSettingsAsync(long routingListId, CancellationToken ct = default) => user.GetRoutingSettingsAsync(routingListId, ct);

    /// <inheritdoc/>
    public Task SetRoutingSettingsAsync(RoutingSettings settings, CancellationToken ct = default) => user.SetRoutingSettingsAsync(settings, ct);

    /// <inheritdoc/>
    public Task RemoveRoutingSettingsAsync(long routingListId, CancellationToken ct = default) => user.RemoveRoutingSettingsAsync(routingListId, ct);

    /// <inheritdoc/>
    public Task<long?> GetSelectedRoutingListAsync(CancellationToken ct = default) => user.GetSelectedRoutingListAsync(ct);

    /// <inheritdoc/>
    public Task SetSelectedRoutingListAsync(long? routingListId, CancellationToken ct = default) => user.SetSelectedRoutingListAsync(routingListId, ct);

    /// <inheritdoc/>
    public Task SaveTunnelStateAsync(TunnelState state, CancellationToken ct = default) => user.SaveTunnelStateAsync(state, ct);

    /// <inheritdoc/>
    public Task<TunnelState?> GetTunnelStateAsync(string name, CancellationToken ct = default) => user.GetTunnelStateAsync(name, ct);

    /// <inheritdoc/>
    public Task<IReadOnlyList<TunnelState>> ListTunnelStatesAsync(CancellationToken ct = default) => user.ListTunnelStatesAsync(ct);

    /// <inheritdoc/>
    public Task RemoveTunnelStateAsync(string name, CancellationToken ct = default) => user.RemoveTunnelStateAsync(name, ct);

    /// <inheritdoc/>
    public Task BackupToAsync(string destinationPath, CancellationToken ct = default) => user.BackupToAsync(destinationPath, ct);

    /// <inheritdoc/>
    public void ClearPool()
    {
        user.ClearPool();
    }
}
