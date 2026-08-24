using AmneziaGeo.Decl;

namespace AmneziaGeo.Linux.App;

/// <summary>
/// The routing rules a connection runs under: the proxy bucket goes through the tunnel, the direct bucket stays
/// off it, the block bucket is refused. Split tunnels only the proxy bucket; otherwise everything but direct goes.
/// </summary>
internal sealed record TunnelRouting(
    bool Split,
    string ListName,
    IReadOnlyList<string> ProxyRoutes,
    IReadOnlyList<GeoDomain> ProxyDomains,
    IReadOnlyList<string> DirectRoutes,
    IReadOnlyList<GeoDomain> DirectDomains,
    IReadOnlyList<string> BlockRoutes,
    IReadOnlyList<GeoDomain> BlockDomains)
{
    /// <summary>
    /// No list selected: a full tunnel by the config's own AllowedIPs.
    /// </summary>
    public static TunnelRouting None { get; } = new(false, string.Empty, [], [], [], [], [], []);

    /// <summary>
    /// Whether any bucket carries a rule.
    /// </summary>
    public bool HasRules =>
        ProxyRoutes.Count > 0 || ProxyDomains.Count > 0
        || DirectRoutes.Count > 0 || DirectDomains.Count > 0
        || BlockRoutes.Count > 0 || BlockDomains.Count > 0;

    /// <summary>
    /// Reads the list a config routes through and the mode it runs in.
    /// </summary>
    public static async Task<TunnelRouting> LoadAsync(IStateStore store, string config, CancellationToken ct)
    {
        var listId = await RoutingBinding.ResolveAsync(store, config, ct).ConfigureAwait(false);
        if (listId is null)
        {
            return None;
        }

        var list = await store.GetRoutingListAsync(listId.Value, ct).ConfigureAwait(false);
        if (list is null)
        {
            return None;
        }

        var settings = await store.GetRoutingSettingsAsync(listId.Value, ct).ConfigureAwait(false);
        var split = !(settings?.UseGlobalProxy ?? false);
        return new TunnelRouting(
            split,
            list.Name,
            list.Routes,
            list.Domains,
            list.DirectRoutes,
            list.DirectDomains,
            list.BlockRoutes,
            list.BlockDomains);
    }
}
