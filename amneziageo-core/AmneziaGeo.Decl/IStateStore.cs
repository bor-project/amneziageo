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
    /// Returns the geo settings and active set for a tunnel, or null if absent.
    /// </summary>
    Task<TunnelGeo?> GetTunnelGeoAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Inserts or updates the geo settings and active set for a tunnel.
    /// </summary>
    Task SaveTunnelGeoAsync(TunnelGeo geo, CancellationToken ct = default);

    /// <summary>
    /// Returns all tunnel names that have geo settings.
    /// </summary>
    Task<IReadOnlyList<string>> ListTunnelGeoNamesAsync(CancellationToken ct = default);

    /// <summary>
    /// Inserts or updates the resolved IPs for a tunnel's domain.
    /// </summary>
    Task SaveDomainResolutionAsync(string tunnel, DomainResolution resolution, CancellationToken ct = default);

    /// <summary>
    /// Returns all saved domain resolutions for a tunnel.
    /// </summary>
    Task<IReadOnlyList<DomainResolution>> ListDomainResolutionsAsync(string tunnel, CancellationToken ct = default);

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
}
