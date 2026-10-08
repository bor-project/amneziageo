namespace AmneziaGeo.Decl;

/// <summary>
/// Whether a config takes the routing list: its own switch.
/// </summary>
public static class ConfigRouting
{
    /// <summary>
    /// Returns whether a config takes the routing list: its switch is on.
    /// </summary>
    public static bool Allowed(ConfigTransport? transport) => transport?.UseRouting ?? true;

    /// <summary>
    /// Returns whether the named config takes the routing list.
    /// </summary>
    public static async Task<bool> AllowedAsync(IStateStore store, string name, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);

        return Allowed(await store.GetConfigTransportAsync(name, ct).ConfigureAwait(false));
    }
}
