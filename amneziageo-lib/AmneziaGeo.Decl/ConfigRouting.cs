namespace AmneziaGeo.Decl;

/// <summary>
/// Whether a config takes the routing list: its own switch and the ban its server may offer.
/// </summary>
public static class ConfigRouting
{
    /// <summary>
    /// Returns whether a config takes the routing list: its switch is on and its server bans nothing.
    /// </summary>
    public static bool Allowed(ConfigTransport? transport, ServerOffer? offer) =>
        (transport?.UseRouting ?? true) && !(offer?.RoutingLocked ?? false);

    /// <summary>
    /// Returns whether the named config takes the routing list.
    /// </summary>
    public static async Task<bool> AllowedAsync(IStateStore store, string name, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);

        var transport = await store.GetConfigTransportAsync(name, ct).ConfigureAwait(false);
        var offer = await ServerOfferStore.ReadAsync(store, name, ct).ConfigureAwait(false);
        return Allowed(transport, offer);
    }
}
