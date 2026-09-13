using System.Security.Cryptography;
using System.Text;
using AmneziaGeo.Decl;

namespace AmneziaGeo.Routing;

/// <summary>
/// Keeps the cache's hottest destinations in the state store, under the tunnel that used them. A verdict an app or a
/// name settled comes back only under the rules it was settled by.
/// </summary>
public sealed class StoredRouteMemory : IRouteMemory
{
    private readonly IStateStore _store;
    private readonly string _tunnel;
    private readonly string _rules;

    /// <summary>
    /// ctor
    /// </summary>
    public StoredRouteMemory(IStateStore store, string tunnel, IReadOnlyList<string> apps, IReadOnlyList<GeoDomain> proxyDomains, IReadOnlyList<GeoDomain> directDomains, IReadOnlyList<GeoDomain> blockDomains)
    {
        _store = store;
        _tunnel = tunnel;
        _rules = Signature(apps, proxyDomains, directDomains, blockDomains);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<RememberedRoute>> LoadAsync(CancellationToken ct)
    {
        var routes = await _store.ListRememberedRoutesAsync(_tunnel, ct).ConfigureAwait(false);
        var settled = await _store.GetSettingAsync(RulesKey(_tunnel), ct).ConfigureAwait(false);
        if (routes.Count == 0 || string.Equals(settled, _rules, StringComparison.Ordinal))
        {
            return routes;
        }

        // Under other rules every address is decided by the ranges alone.
        return [.. routes.Select(route => route with { ByName = false, ByApp = false })];
    }

    /// <inheritdoc/>
    public async Task SaveAsync(IReadOnlyList<RememberedRoute> routes, CancellationToken ct)
    {
        await _store.SaveRememberedRoutesAsync(_tunnel, routes, ct).ConfigureAwait(false);
        await _store.SetSettingAsync(RulesKey(_tunnel), _rules, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Settings key carrying the rules the remembered destinations of a tunnel were decided under.
    /// </summary>
    public static string RulesKey(string tunnel)
    {
        return $"route-memory-rules:{tunnel}";
    }

    // Fingerprint of the rules that decide an address past the ranges.
    private static string Signature(IReadOnlyList<string> apps, IReadOnlyList<GeoDomain> proxyDomains, IReadOnlyList<GeoDomain> directDomains, IReadOnlyList<GeoDomain> blockDomains)
    {
        var tokens = new List<string>(apps.Count + proxyDomains.Count + directDomains.Count + blockDomains.Count);
        tokens.AddRange(apps.Select(app => "app|" + app.Trim().ToLowerInvariant()));
        tokens.AddRange(proxyDomains.Select(domain => Token("proxy", domain)));
        tokens.AddRange(directDomains.Select(domain => Token("direct", domain)));
        tokens.AddRange(blockDomains.Select(domain => Token("block", domain)));
        tokens.Sort(StringComparer.Ordinal);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', tokens)));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    // One rule of the fingerprint.
    private static string Token(string role, GeoDomain domain)
    {
        return $"{role}|{domain.Kind}:{domain.Value.Trim().ToLowerInvariant()}";
    }
}
