using System.Globalization;
using AmneziaGeo.Decl;

namespace AmneziaGeo.Geo;

/// <summary>
/// Puts the routing list of unavailable sites in a fresh install, once: a list the owner removes stays removed, and an
/// install that held lists or configurations before is left alone.
/// </summary>
public static class RoutingSeed
{
    /// <summary>
    /// The version of what a fresh install is given.
    /// </summary>
    public const int SeedVersion = 1;

    private const string SeedVersionKey = "routing.seed-version";

    /// <summary>
    /// Adds the list under the name to a store that never held a list or a configuration and applies it. A host
    /// that keeps its configurations outside the store says through <paramref name="inUse"/> whether it holds any:
    /// an install in use without a list sends everything through the tunnel, and a list would narrow that. Returns
    /// whether it was added.
    /// </summary>
    public static async Task<bool> SeedAsync(IStateStore store, GeoConfigurator geo, string name, CancellationToken ct, bool inUse = false)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(geo);

        if (!string.IsNullOrEmpty(await store.GetSettingAsync(SeedVersionKey, ct).ConfigureAwait(false)))
        {
            return false;
        }

        var lists = await store.ListRoutingListsAsync(ct).ConfigureAwait(false);
        var configs = await store.ListConfigNamesAsync(ct).ConfigureAwait(false);
        var fresh = !inUse && lists.Count == 0 && configs.Count == 0;
        if (fresh)
        {
            var id = await OfferedLists.AddListAsync(store, geo, name, RoutingDefaults.Unavailable, true, false, ct).ConfigureAwait(false);
            await store.SetSelectedRoutingListAsync(id, ct).ConfigureAwait(false);
        }

        await store.SetSettingAsync(SeedVersionKey, SeedVersion.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);

        return fresh;
    }
}
