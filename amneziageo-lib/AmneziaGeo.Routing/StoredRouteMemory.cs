using AmneziaGeo.Decl;

namespace AmneziaGeo.Routing;

/// <summary>
/// Keeps the cache's hottest destinations in the state store, under the tunnel that used them.
/// </summary>
public sealed class StoredRouteMemory(IStateStore store, string tunnel) : IRouteMemory
{
    /// <inheritdoc/>
    public Task<IReadOnlyList<RememberedRoute>> LoadAsync(CancellationToken ct) => store.ListRememberedRoutesAsync(tunnel, ct);

    /// <inheritdoc/>
    public Task SaveAsync(IReadOnlyList<RememberedRoute> routes, CancellationToken ct) => store.SaveRememberedRoutesAsync(tunnel, routes, ct);
}
