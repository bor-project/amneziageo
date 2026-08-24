namespace AmneziaGeo.Decl;

/// <summary>
/// Resolves the routing list a configuration routes through.
/// </summary>
public static class RoutingBinding
{
    /// <summary>
    /// Returns the configuration's own routing list, or the default one while it carries no binding; null sends
    /// every destination through the tunnel.
    /// </summary>
    public static async Task<long?> ResolveAsync(IStateStore store, string config, CancellationToken ct = default)
    {
        var binding = await store.GetConfigRoutingAsync(config, ct).ConfigureAwait(false);
        if (binding is not null)
        {
            return binding.RoutingListId;
        }

        return await store.GetSelectedRoutingListAsync(ct).ConfigureAwait(false);
    }
}
