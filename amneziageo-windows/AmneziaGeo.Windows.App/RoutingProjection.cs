using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Writes onto a tunnel the share of the selected routing list it carries.
/// </summary>
internal static class RoutingProjection
{
    /// <summary>
    /// Projects the selected list onto the named tunnel.
    /// </summary>
    public static async Task ProjectAsync(IStateStore store, GeoConfigurator geo, TunnelDutyRoster roster, string config, ILogger logger, CancellationToken ct)
    {
        var listId = await store.GetSelectedRoutingListAsync(ct);
        if (listId is null)
        {
            // No list picked: project full tunnel, override config set-geo.
            await ProjectFullTunnelAsync(store, config, logger, ct);
            return;
        }

        var list = await store.GetRoutingListAsync(listId.Value, ct);
        if (list is null)
        {
            logger.LogWarning("routing list {Id} no longer exists; until another list is picked, everything goes through the tunnel", listId.Value);
            await ProjectFullTunnelAsync(store, config, logger, ct);
            return;
        }

        // The share of the list this tunnel carries. A machine running one tunnel is given the list itself,
        // and the rules are the ones already expanded into it.
        var share = roster.Share(config, list.Id, list.Rules);
        if (!ReferenceEquals(share, list.Rules))
        {
            logger.LogInformation("{Config} carries {Kept} of the {Total} rule(s) of '{List}'; the rest ride other servers of the set", config, share.Count, list.Rules.Count, list.Name);
            list = await geo.MaterializeDraftAsync([.. share.Select(GeoConfigurator.FormatWithRole)], ct) with { Id = list.Id, Name = list.Name };
        }

        await store.SaveTunnelProjectionAsync(config, true, list.Routes, list.Domains, list.Apps,
            list.DirectRoutes, list.DirectDomains, list.BlockRoutes, list.BlockDomains, list.Id, ct);
        logger.LogInformation("routing list '{List}' now applies to {Config}: only what it names goes through the tunnel", list.Name, config);
    }

    private static async Task ProjectFullTunnelAsync(IStateStore store, string config, ILogger logger, CancellationToken ct)
    {
        // geoSplit=false -> full tunnel via config AllowedIPs.
        await store.SaveTunnelProjectionAsync(config, false, [], [], [], [], [], [], [], null, ct);
        logger.LogInformation("routing rules are off for {Config}: all traffic goes through the tunnel", config);
    }
}
