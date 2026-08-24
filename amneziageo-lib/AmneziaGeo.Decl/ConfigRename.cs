namespace AmneziaGeo.Decl;

/// <summary>
/// Moves the state keyed by a configuration name onto its new name.
/// </summary>
public static class ConfigRename
{
    /// <summary>
    /// Carries the per-config settings, the resolved addresses, the live state and the agent's selection from the
    /// old name to the new one. The configuration row itself is renamed by the caller.
    /// </summary>
    public static async Task CarryAsync(IStateStore store, string oldName, string newName, CancellationToken ct = default)
    {
        var geo = await store.GetTunnelGeoAsync(oldName, ct).ConfigureAwait(false);
        if (geo is not null)
        {
            await store.SaveTunnelGeoAsync(geo with { Name = newName }, ct).ConfigureAwait(false);
            await store.RemoveTunnelGeoAsync(oldName, ct).ConfigureAwait(false);
        }

        var transport = await store.GetConfigTransportAsync(oldName, ct).ConfigureAwait(false);
        if (transport is not null)
        {
            await store.SetConfigTransportAsync(transport with { Name = newName }, ct).ConfigureAwait(false);
            await store.RemoveConfigTransportAsync(oldName, ct).ConfigureAwait(false);
        }

        var dns = await store.GetConfigDnsAsync(oldName, ct).ConfigureAwait(false);
        if (dns is not null)
        {
            await store.SetConfigDnsAsync(dns with { Name = newName }, ct).ConfigureAwait(false);
            await store.RemoveConfigDnsAsync(oldName, ct).ConfigureAwait(false);
        }

        var exclusions = await store.GetConfigExclusionsAsync(oldName, ct).ConfigureAwait(false);
        if (exclusions is not null)
        {
            await store.SetConfigExclusionsAsync(exclusions with { Name = newName }, ct).ConfigureAwait(false);
            await store.RemoveConfigExclusionsAsync(oldName, ct).ConfigureAwait(false);
        }

        var routing = await store.GetConfigRoutingAsync(oldName, ct).ConfigureAwait(false);
        if (routing is not null)
        {
            await store.SetConfigRoutingAsync(routing with { Name = newName }, ct).ConfigureAwait(false);
            await store.RemoveConfigRoutingAsync(oldName, ct).ConfigureAwait(false);
        }

        foreach (var resolution in await store.ListDomainResolutionsAsync(oldName, ct).ConfigureAwait(false))
        {
            await store.SaveDomainResolutionAsync(newName, resolution, 0, ct).ConfigureAwait(false);
        }

        await store.RemoveDomainResolutionsAsync(oldName, ct).ConfigureAwait(false);

        var state = await store.GetTunnelStateAsync(oldName, ct).ConfigureAwait(false);
        if (state is not null)
        {
            await store.SaveTunnelStateAsync(state with { Name = newName }, ct).ConfigureAwait(false);
            await store.RemoveTunnelStateAsync(oldName, ct).ConfigureAwait(false);
        }

        var selected = await store.GetSettingAsync(StateKeys.SelectedTarget, ct).ConfigureAwait(false);
        if (string.Equals(selected, oldName, StringComparison.Ordinal))
        {
            await store.SetSettingAsync(StateKeys.SelectedTarget, newName, ct).ConfigureAwait(false);
        }
    }
}
