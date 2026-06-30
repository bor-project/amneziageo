namespace AmneziaGeo.Decl;

/// <summary>
/// Persistent store for tunnel profiles, geo settings, and geo file metadata.
/// </summary>
public interface IStateStore
{
    /// <summary>
    /// Ensures the backing schema exists.
    /// </summary>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the named profile, or null if absent.
    /// </summary>
    Task<TunnelProfile?> GetProfileAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Inserts or updates a profile.
    /// </summary>
    Task SaveProfileAsync(TunnelProfile profile, CancellationToken ct = default);

    /// <summary>
    /// Returns all stored profile names.
    /// </summary>
    Task<IReadOnlyList<string>> ListProfileNamesAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the config's own (user-configured) geo settings and active set, or null if absent.
    /// Ignores any live balancer routing projection — use <see cref="GetActiveTunnelGeoAsync"/>
    /// for what the running tunnel should apply.
    /// </summary>
    Task<TunnelGeo?> GetTunnelGeoAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Returns the geo set the running tunnel should apply: the active balancer routing projection
    /// when one is present, otherwise the config's own set-geo split. Null if neither exists.
    /// </summary>
    Task<TunnelGeo?> GetActiveTunnelGeoAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Inserts or updates the config's own (user-configured) geo settings and active set. Leaves
    /// any live balancer routing projection untouched.
    /// </summary>
    Task SaveTunnelGeoAsync(TunnelGeo geo, CancellationToken ct = default);

    /// <summary>
    /// Stores a balancer routing projection for a tunnel and marks it live. Written only by the
    /// balancer; never touches the config's own set-geo columns. <paramref name="routingListId"/> is the
    /// id of the routing list this projection came from (null for a full-tunnel / no-list projection), so
    /// the running tunnel can resolve the active list's traffic settings (#87/#89).
    /// </summary>
    Task SaveTunnelProjectionAsync(string name, bool split, IReadOnlyList<string> routes, IReadOnlyList<GeoDomain> domains, IReadOnlyList<string> apps, long? routingListId, CancellationToken ct = default);

    /// <summary>
    /// Drops the live balancer routing projection for a tunnel, reverting it to its own set-geo
    /// split (or no split). A no-op when no row exists. Never touches the config's own columns.
    /// </summary>
    Task ClearTunnelProjectionAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Returns the routing list id of the tunnel's live projection, or null when there is no live
    /// projection or it is a full-tunnel / no-list projection. Used at connect to resolve the active
    /// routing list's traffic settings (local DNS, exclusions, all-UDP).
    /// </summary>
    Task<long?> GetActiveRoutingListIdAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Returns all tunnel names that have geo settings.
    /// </summary>
    Task<IReadOnlyList<string>> ListTunnelGeoNamesAsync(CancellationToken ct = default);

    /// <summary>
    /// Removes the geo settings for a tunnel.
    /// </summary>
    Task RemoveTunnelGeoAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Returns the config's transport settings (WebSocket over TCP), or null when none are stored
    /// (the config uses plain UDP).
    /// </summary>
    Task<ConfigTransport?> GetConfigTransportAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Inserts or updates a config's transport settings.
    /// </summary>
    Task SetConfigTransportAsync(ConfigTransport transport, CancellationToken ct = default);

    /// <summary>
    /// Removes a config's transport settings.
    /// </summary>
    Task RemoveConfigTransportAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Returns a config's preferred DNS settings, or null when none are stored (the config auto-detects
    /// the system resolvers for non-tunneled names).
    /// </summary>
    Task<ConfigDns?> GetConfigDnsAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Inserts or updates a config's preferred DNS settings.
    /// </summary>
    Task SetConfigDnsAsync(ConfigDns dns, CancellationToken ct = default);

    /// <summary>
    /// Removes a config's preferred DNS settings.
    /// </summary>
    Task RemoveConfigDnsAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Returns a config's bypass exclusions, or null when none are stored (the config uses just the
    /// built-in defaults with auto-exclude-LAN on).
    /// </summary>
    Task<ConfigExclusions?> GetConfigExclusionsAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Inserts or updates a config's bypass exclusions.
    /// </summary>
    Task SetConfigExclusionsAsync(ConfigExclusions exclusions, CancellationToken ct = default);

    /// <summary>
    /// Removes a config's bypass exclusions.
    /// </summary>
    Task RemoveConfigExclusionsAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Returns whether a stored configuration (wg-quick text) with the given name exists.
    /// </summary>
    Task<bool> ConfigExistsAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Returns the stored wg-quick text of a configuration, or null if absent.
    /// </summary>
    Task<string?> GetConfigTextAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Returns the names of all stored configurations, ordered by name.
    /// </summary>
    Task<IReadOnlyList<string>> ListConfigNamesAsync(CancellationToken ct = default);

    /// <summary>
    /// Inserts or updates the wg-quick text of a configuration.
    /// </summary>
    Task SaveConfigAsync(string name, string text, CancellationToken ct = default);

    /// <summary>
    /// Renames a stored configuration's row. The destination name must be free.
    /// </summary>
    Task RenameConfigAsync(string oldName, string newName, CancellationToken ct = default);

    /// <summary>
    /// Removes a stored configuration by name.
    /// </summary>
    Task RemoveConfigAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Inserts or updates a geo download source.
    /// </summary>
    Task SaveGeoSourceAsync(GeoSource source, CancellationToken ct = default);

    /// <summary>
    /// Returns all geo download sources ordered by position.
    /// </summary>
    Task<IReadOnlyList<GeoSource>> ListGeoSourcesAsync(CancellationToken ct = default);

    /// <summary>
    /// Removes a geo download source by name.
    /// </summary>
    Task RemoveGeoSourceAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Inserts or updates the resolved IPs for a tunnel's domain.
    /// </summary>
    Task SaveDomainResolutionAsync(string tunnel, DomainResolution resolution, CancellationToken ct = default);

    /// <summary>
    /// Returns all saved domain resolutions for a tunnel.
    /// </summary>
    Task<IReadOnlyList<DomainResolution>> ListDomainResolutionsAsync(string tunnel, CancellationToken ct = default);

    /// <summary>
    /// Removes all saved domain resolutions for a tunnel.
    /// </summary>
    Task RemoveDomainResolutionsAsync(string tunnel, CancellationToken ct = default);

    /// <summary>
    /// Inserts or updates a failover balancer group.
    /// </summary>
    Task SaveBalancerAsync(BalancerGroup balancer, CancellationToken ct = default);

    /// <summary>
    /// Returns the named balancer group, or null if absent.
    /// </summary>
    Task<BalancerGroup?> GetBalancerAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Returns all stored balancer names.
    /// </summary>
    Task<IReadOnlyList<string>> ListBalancerNamesAsync(CancellationToken ct = default);

    /// <summary>
    /// Removes a balancer group by name.
    /// </summary>
    Task RemoveBalancerAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Inserts or updates a routing list. If Id is 0 a new row is inserted; otherwise the
    /// row with that id is updated. Returns the row id after the call.
    /// </summary>
    Task<long> SaveRoutingListAsync(RoutingList list, CancellationToken ct = default);

    /// <summary>
    /// Returns the routing list by id, or null if absent.
    /// </summary>
    Task<RoutingList?> GetRoutingListAsync(long id, CancellationToken ct = default);

    /// <summary>
    /// Returns the routing list by name, or null if absent.
    /// </summary>
    Task<RoutingList?> GetRoutingListByNameAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Returns all stored routing lists ordered by name.
    /// </summary>
    Task<IReadOnlyList<RoutingList>> ListRoutingListsAsync(CancellationToken ct = default);

    /// <summary>
    /// Removes a routing list by id. Profiles referencing it have their assignment cleared.
    /// </summary>
    Task RemoveRoutingListAsync(long id, CancellationToken ct = default);

    /// <summary>
    /// Returns a routing list's traffic settings (local DNS, exclusions, all-UDP, mode), or null when
    /// none are stored (the list uses defaults: split mode, runtime-default exclusions, no all-UDP).
    /// </summary>
    Task<RoutingSettings?> GetRoutingSettingsAsync(long routingListId, CancellationToken ct = default);

    /// <summary>
    /// Inserts or updates a routing list's traffic settings.
    /// </summary>
    Task SetRoutingSettingsAsync(RoutingSettings settings, CancellationToken ct = default);

    /// <summary>
    /// Removes a routing list's traffic settings.
    /// </summary>
    Task RemoveRoutingSettingsAsync(long routingListId, CancellationToken ct = default);

    /// <summary>
    /// One-time, idempotent migration (#87): seeds each routing list assigned to a profile with traffic
    /// settings taken from its member config's DNS / exclusions and the legacy global all-UDP flag, when the
    /// list carries none yet. Lists that already have settings are left untouched. Behaviour-neutral - the
    /// running tunnel reads the same values, now sourced from the routing list instead of the per-config tables.
    /// </summary>
    Task MigrateConfigSettingsToRoutingAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the assigned routing list id (or null) and the use-routing flag for a profile.
    /// </summary>
    Task<(long? RoutingListId, bool UseRouting)> GetProfileRoutingAsync(string profile, CancellationToken ct = default);

    /// <summary>
    /// Sets the assigned routing list id (or null to clear) and the use-routing flag for a profile.
    /// </summary>
    Task SetProfileRoutingAsync(string profile, long? routingListId, bool useRouting, CancellationToken ct = default);

    /// <summary>
    /// Returns metadata for a geo file, or null if absent.
    /// </summary>
    Task<GeoFileMetadata?> GetGeoFileAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Inserts or updates geo file metadata.
    /// </summary>
    Task SaveGeoFileAsync(GeoFileMetadata metadata, CancellationToken ct = default);

    /// <summary>
    /// Returns metadata for all stored geo files.
    /// </summary>
    Task<IReadOnlyList<GeoFileMetadata>> ListGeoFilesAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns a stored application setting value, or null if absent.
    /// </summary>
    Task<string?> GetSettingAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// Inserts or updates an application setting value.
    /// </summary>
    Task SetSettingAsync(string key, string value, CancellationToken ct = default);

    /// <summary>
    /// Inserts or updates the live runtime state for a balancer group.
    /// </summary>
    Task SaveBalancerStateAsync(BalancerState state, CancellationToken ct = default);

    /// <summary>
    /// Returns the live runtime state for a balancer group, or null if absent.
    /// </summary>
    Task<BalancerState?> GetBalancerStateAsync(string group, CancellationToken ct = default);

    /// <summary>
    /// Returns the live runtime state for every balancer group.
    /// </summary>
    Task<IReadOnlyList<BalancerState>> ListBalancerStatesAsync(CancellationToken ct = default);

    /// <summary>
    /// Writes a consistent snapshot of the database to the given destination path.
    /// </summary>
    Task BackupToAsync(string destinationPath, CancellationToken ct = default);

    /// <summary>
    /// Releases pooled database connections so the database file can be replaced.
    /// </summary>
    void ClearPool();
}
