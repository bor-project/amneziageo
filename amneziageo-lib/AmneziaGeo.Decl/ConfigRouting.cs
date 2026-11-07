namespace AmneziaGeo.Decl;

/// <summary>
/// Whether a config takes the routing list: its own switch.
/// </summary>
public static class ConfigRouting
{
    private const string Inherited = "inherit";

    /// <summary>
    /// Returns the configuration's routing selection key.
    /// </summary>
    public static string SelectionKey(string name) => StateKeys.ConfigRoutingPrefix + name;

    /// <summary>
    /// Returns the selected configuration's routing list.
    /// </summary>
    public static async Task<long?> CurrentListAsync(IStateStore store, CancellationToken ct = default)
    {
        var name = await store.GetSettingAsync(StateKeys.SelectedTarget, ct).ConfigureAwait(false);
        return await SelectedListAsync(store, name, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Stores the selected configuration's routing list.
    /// </summary>
    public static async Task SelectCurrentListAsync(IStateStore store, long? listId, CancellationToken ct = default)
    {
        var name = await store.GetSettingAsync(StateKeys.SelectedTarget, ct).ConfigureAwait(false);
        await SelectListAsync(store, name, listId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Clears the removed configuration's selection.
    /// </summary>
    public static async Task ForgetListAsync(IStateStore store, string name, CancellationToken ct = default)
    {
        await store.SetSettingAsync(SelectionKey(name), Inherited, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Moves the selection to the renamed configuration.
    /// </summary>
    public static async Task RenameListAsync(IStateStore store, string oldName, string newName, CancellationToken ct = default)
    {
        var oldValue = await store.GetSettingAsync(SelectionKey(oldName), ct).ConfigureAwait(false);
        var newValue = await store.GetSettingAsync(SelectionKey(newName), ct).ConfigureAwait(false);
        if (oldValue != Inherited && (oldValue is not null || newValue is null || newValue == Inherited))
        {
            await SelectListAsync(store, newName, await SelectedListAsync(store, oldName, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        }

        await ForgetListAsync(store, oldName, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the list selected for the configuration.
    /// </summary>
    public static async Task<long?> SelectedListAsync(IStateStore store, string? name, CancellationToken ct = default)
    {
        var value = string.IsNullOrEmpty(name) ? null : await store.GetSettingAsync(SelectionKey(name), ct).ConfigureAwait(false);
        if (value == Inherited)
        {
            value = null;
        }
        value ??= await store.GetSettingAsync(StateKeys.ConfigRoutingDefault, ct).ConfigureAwait(false);
        if (value is null)
        {
            return await store.GetSelectedRoutingListAsync(ct).ConfigureAwait(false);
        }

        return long.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;
    }

    /// <summary>
    /// Stores the list selected for the configuration.
    /// </summary>
    public static async Task SelectListAsync(IStateStore store, string? name, long? listId, CancellationToken ct = default)
    {
        var value = listId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        if (string.IsNullOrEmpty(name))
        {
            await store.SetSettingAsync(StateKeys.SelectedRoutingList, value, ct).ConfigureAwait(false);
            await store.SetSettingAsync(StateKeys.ConfigRoutingDefault, value, ct).ConfigureAwait(false);
            return;
        }

        if (await store.GetSettingAsync(StateKeys.ConfigRoutingDefault, ct).ConfigureAwait(false) is null)
        {
            var inherited = (await store.GetSelectedRoutingListAsync(ct).ConfigureAwait(false))?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            await store.SetSettingAsync(StateKeys.ConfigRoutingDefault, inherited, ct).ConfigureAwait(false);
        }

        await store.SetSettingAsync(SelectionKey(name), value, ct).ConfigureAwait(false);
        await store.SetSettingAsync(StateKeys.SelectedRoutingList, value, ct).ConfigureAwait(false);
    }

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
