namespace AmneziaGeo.Decl;

/// <summary>
/// Persistent store for tunnel profiles.
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
}
