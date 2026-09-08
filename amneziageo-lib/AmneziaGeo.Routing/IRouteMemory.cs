using AmneziaGeo.Decl;

namespace AmneziaGeo.Routing;

/// <summary>
/// Where the cache keeps its hottest destinations between sessions, so a reconnect and a restart find them
/// decided instead of unknown.
/// </summary>
public interface IRouteMemory
{
    /// <summary>
    /// Destinations remembered from the previous session.
    /// </summary>
    Task<IReadOnlyList<RememberedRoute>> LoadAsync(CancellationToken ct);

    /// <summary>
    /// Replaces what is remembered with these destinations.
    /// </summary>
    Task SaveAsync(IReadOnlyList<RememberedRoute> routes, CancellationToken ct);
}
