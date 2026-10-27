using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using AmneziaGeo.Routing;
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
        if (!await ConfigRouting.AllowedAsync(store, config, ct).ConfigureAwait(false))
        {
            await ProjectFullTunnelAsync(store, config, logger, ct);
            return;
        }

        var listId = await store.GetSelectedRoutingListAsync(ct);
        if (listId is null)
        {
            // No list picked: project full tunnel, override config set-geo.
            await ProjectFullTunnelAsync(store, config, logger, ct);
            return;
        }

        var stored = await store.GetRoutingListAsync(listId.Value, ct);
        if (stored is null)
        {
            logger.LogWarning("routing list {Id} no longer exists; until another list is picked, everything goes through the tunnel", listId.Value);
            await ProjectFullTunnelAsync(store, config, logger, ct);
            return;
        }

        // A program the list names stands for what this machine found for it last.
        var list = await AppFinds.ExpandListAsync(store, stored, ct).ConfigureAwait(false);

        // The share of the list this tunnel carries. A machine running one tunnel is given the list itself,
        // and the rules are the ones already expanded into it.
        var share = await roster.ShareAsync(store, config, list.Id, list.Rules, ct).ConfigureAwait(false);
        if (!ReferenceEquals(share, list.Rules))
        {
            logger.LogInformation("{Config} carries {Kept} of the {Total} rule(s) of '{List}'; the rest go where their address or its fallback sends them", config, share.Count, list.Rules.Count, list.Name);
            var projected = await geo.MaterializeDraftAsync([.. share.Select(GeoConfigurator.FormatWithRole)], ct).ConfigureAwait(false);

            // The tunnel ranges of the whole list keep their holes in the direct and blocked ones, whichever tunnel carries them.
            var (_, direct, blocked) = RangeClaims.Settle(list.Routes, projected.DirectRoutes, projected.BlockRoutes);
            list = projected with { Id = list.Id, Name = list.Name, DirectRoutes = direct, BlockRoutes = blocked };
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
