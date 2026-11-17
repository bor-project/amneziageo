namespace AmneziaGeo.Decl;

/// <summary>
/// Reports a failed catalog source.
/// </summary>
public sealed record ProxyCatalogRefreshFailure(string Config, string Code);

/// <summary>
/// Reports the refreshed catalog and failed sources.
/// </summary>
public sealed record ProxyCatalogRefreshResult(IReadOnlyList<TransportProxySummary> Items,
    IReadOnlyList<ProxyCatalogRefreshFailure> Failures);
