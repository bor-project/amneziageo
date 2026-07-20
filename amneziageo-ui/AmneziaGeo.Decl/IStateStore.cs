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
    /// Returns the config's own geo settings, ignoring any live profile projection.
    /// </summary>
    Task<TunnelGeo?> GetTunnelGeoAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Returns the geo set the running tunnel should apply: profile projection if present, else the config's own split.
    /// </summary>
    Task<TunnelGeo?> GetActiveTunnelGeoAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Inserts or updates the config's own geo settings. Leaves any live profile projection untouched.
    /// </summary>
    Task SaveTunnelGeoAsync(TunnelGeo geo, CancellationToken ct = default);

    /// <summary>
    /// Stores a profile routing projection and marks it live. routingListId is the source list (null for full-tunnel / no-list).
    /// </summary>
    Task SaveTunnelProjectionAsync(string name, bool split, IReadOnlyList<string> routes, IReadOnlyList<GeoDomain> domains, IReadOnlyList<string> apps, long? routingListId, CancellationToken ct = default);

    /// <summary>
    /// Drops the live profile projection, reverting to the config's own split. No-op when no row exists.
    /// </summary>
    Task ClearTunnelProjectionAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Returns the routing list id of the tunnel's live projection, or null when none or full-tunnel.
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
    /// Returns the config's transport settings, or null when none (plain UDP).
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
    /// Returns the config's preferred DNS, or null when none (auto-detect).
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
    /// Returns the config's bypass exclusions, or null when none (built-in defaults with auto-exclude-LAN).
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
    /// Inserts or updates the resolved IPs for a tunnel's domain, tagged with the routing list it came from
    /// (0 = none/unknown). The list id lets a list's cached resolutions be cleaned when the list is removed.
    /// </summary>
    Task SaveDomainResolutionAsync(string tunnel, DomainResolution resolution, long listId, CancellationToken ct = default);

    /// <summary>
    /// Returns all saved domain resolutions for a tunnel.
    /// </summary>
    Task<IReadOnlyList<DomainResolution>> ListDomainResolutionsAsync(string tunnel, CancellationToken ct = default);

    /// <summary>
    /// Removes all saved domain resolutions for a tunnel.
    /// </summary>
    Task RemoveDomainResolutionsAsync(string tunnel, CancellationToken ct = default);

    /// <summary>
    /// Removes a single domain's cached resolution for a tunnel (domain left the routing lists).
    /// </summary>
    Task DeleteDomainResolutionAsync(string tunnel, string domain, CancellationToken ct = default);

    /// <summary>
    /// Returns a single tunnel/domain's cached resolution, or null when none is stored. Backs the on-demand
    /// cache hydrate: a queried domain's last-good IPs are restored without a re-resolve. The lookup is served
    /// by the (tunnel, domain) prefix of the UNIQUE(tunnel, domain, ip) index, so it stays a point read.
    /// </summary>
    Task<DomainResolution?> GetDomainResolutionAsync(string tunnel, string domain, CancellationToken ct = default);

    /// <summary>
    /// Inserts or updates a profile.
    /// </summary>
    Task SaveProfileAsync(Profile profile, CancellationToken ct = default);

    /// <summary>
    /// Returns the named profile, or null if absent.
    /// </summary>
    Task<Profile?> GetProfileAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Returns all stored profile names.
    /// </summary>
    Task<IReadOnlyList<string>> ListProfileNamesAsync(CancellationToken ct = default);

    /// <summary>
    /// Removes a profile by name.
    /// </summary>
    Task RemoveProfileAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Inserts or updates a routing list. Returns the row id.
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
    /// Returns id, name, and rule/route/domain counts for every routing list, without deserializing the rule
    /// rows or the materialized JSON. Backs the status snapshot, which needs only the counts.
    /// </summary>
    Task<IReadOnlyList<(long Id, string Name, int RuleCount, int RouteCount, int DomainCount)>> ListRoutingListSummariesAsync(CancellationToken ct = default);

    /// <summary>
    /// Removes a routing list by id. Profile assignments are cleared.
    /// </summary>
    Task RemoveRoutingListAsync(long id, CancellationToken ct = default);

    /// <summary>
    /// Returns the current materialized set and generation of the routing list projected onto a tunnel, or null when none.
    /// </summary>
    Task<ActiveRoutingListMaterialization?> GetActiveRoutingListMaterializationAsync(string tunnel, CancellationToken ct = default);

    /// <summary>
    /// Returns just the generation of the routing list projected onto a tunnel (no route/domain deserialization),
    /// so the change poll can skip the full materialization read when nothing changed. Null when none is projected.
    /// </summary>
    Task<long?> GetActiveRoutingListGenerationAsync(string tunnel, CancellationToken ct = default);

    /// <summary>
    /// Returns ids of routing lists assigned to at least one profile.
    /// </summary>
    Task<IReadOnlyList<long>> ListAssignedRoutingListIdsAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns a routing list's traffic settings, or null when none (defaults: split mode, no all-UDP).
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
    /// Returns all application settings as a key/value map, so a caller reading several settings does one query.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> GetSettingsAsync(CancellationToken ct = default);

    /// <summary>
    /// Inserts or updates an application setting value.
    /// </summary>
    Task SetSettingAsync(string key, string value, CancellationToken ct = default);

    /// <summary>
    /// Inserts or updates the live runtime state for a profile.
    /// </summary>
    Task SaveProfileStateAsync(ProfileState state, CancellationToken ct = default);

    /// <summary>
    /// Returns the live runtime state for a profile, or null if absent.
    /// </summary>
    Task<ProfileState?> GetProfileStateAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Returns the live runtime state for every profile.
    /// </summary>
    Task<IReadOnlyList<ProfileState>> ListProfileStatesAsync(CancellationToken ct = default);

    /// <summary>
    /// Writes a consistent snapshot of the database to the given destination path.
    /// </summary>
    Task BackupToAsync(string destinationPath, CancellationToken ct = default);

    /// <summary>
    /// Releases pooled database connections so the database file can be replaced.
    /// </summary>
    void ClearPool();
}
