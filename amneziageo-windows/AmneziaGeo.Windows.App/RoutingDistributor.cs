using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using AmneziaGeo.Ipc;
using AmneziaGeo.Routing;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Splits the routing list across the servers that are up and hands every tunnel its share. One list applies to the
/// whole machine while several servers work at once; with the mode off each tunnel keeps the list its own
/// configuration is bound to and carries all of it, which is the answer the machine gave before the mode existed.
/// </summary>
internal sealed class RoutingDistributor(
    AgentControl control,
    ScopedStoreFactory stores,
    SettingsStore settingsStore,
    ServiceManager serviceManager,
    IGeoFileStore geoFiles,
    ILogger<RoutingDistributor> logger)
{
    // One pass at a time: rounds, list edits and tunnels coming up all ask for the same recount.
    private readonly SemaphoreSlim _gate = new(1, 1);

    // The plan each list was last split by. Rounds are frequent and the expansion is expensive, so a plan that did
    // not move is not materialized again.
    private readonly Dictionary<string, int> _planned = new(StringComparer.Ordinal);

    // What each tunnel was handed last, so a share that did not move is neither rewritten nor announced.
    private readonly Dictionary<string, int> _handed = new(StringComparer.Ordinal);

    // The verdict each rule was journalled under: a rule is written about when it changes side, not every round.
    private readonly Dictionary<string, RuleTarget> _journalled = new(StringComparer.Ordinal);

    // What a tunnel is told when the split could not be worked out: it carries everything, as it did before the
    // list was split at all, and takes the default route only if nobody holds it.
    private static readonly TunnelRole Unsplit = new(null, false, false);

    /// <summary>
    /// Recounts every up tunnel's share and hands over what changed. The configuration being raised counts as up:
    /// its share has to stand before it is brought up. Returns the place that configuration takes on the machine.
    /// </summary>
    /// <param name="ownerRoot">Library the list and the configurations are read from.</param>
    /// <param name="raising">Configuration being brought up, if any.</param>
    /// <param name="force">Materialize even where the plan reads the same: the expansion behind it changed.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<TunnelRole> DistributeAsync(string ownerRoot, string? raising = null, bool force = false, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await RunAsync(ownerRoot, raising, force, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "the routing list could not be split across the servers; every tunnel keeps the rules it already has");
            return Unsplit;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The configurations the machine keeps up, priority top down: the server carrying everything plus the ones
    /// rules name, less the cards switched off. Nothing depends on what is up right now, so the answer is the same
    /// before and after a tunnel comes and goes.
    /// </summary>
    /// <param name="ownerRoot">Library the configurations and the list are read from.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<IReadOnlyList<string>> RosterAsync(string ownerRoot, CancellationToken ct = default)
    {
        var store = stores.For(ownerRoot);
        var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var order = await new ConfigRepository(store, serviceManager).ListAsync(ct).ConfigureAwait(false);
        var rules = await RulesAsync(store, settings.MultiServer, ct).ConfigureAwait(false);
        var off = await store.GetSettingAsync(StateKeys.VpnOff, ct).ConfigureAwait(false);
        var picked = await store.GetSettingAsync(StateKeys.SelectedTarget, ct).ConfigureAwait(false);
        return ServerRoster.Build(settings.MultiServer, off is { Length: > 0 }, order, rules, NameList.Split(settings.FailoverSkipped), picked);
    }

    // The rules of the list the whole machine routes through. With the mode off no rule names a server, so none is
    // read: the buckets they expand into are what the tunnel routes by, and they are read where they are applied.
    private static async Task<IReadOnlyList<GeoRule>> RulesAsync(IStateStore store, bool multiServer, CancellationToken ct)
    {
        if (!multiServer)
        {
            return [];
        }

        var selected = await store.GetSelectedRoutingListAsync(ct).ConfigureAwait(false);
        if (selected is null)
        {
            return [];
        }

        return await store.GetRoutingRulesAsync(selected.Value, ct).ConfigureAwait(false);
    }

    private async Task<TunnelRole> RunAsync(string ownerRoot, string? raising, bool force, CancellationToken ct)
    {
        var store = stores.For(ownerRoot);
        var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var order = await new ConfigRepository(store, serviceManager).ListAsync(ct).ConfigureAwait(false);
        var up = Up(order, raising);
        Forget(up);
        if (up.Count == 0)
        {
            return Unsplit;
        }

        var geo = new GeoConfigurator(store, geoFiles);
        if (settings.MultiServer)
        {
            var fleet = new ServerFleet(true, order, up);
            var selected = await store.GetSelectedRoutingListAsync(ct).ConfigureAwait(false);
            await ShareAsync(store, geo, "*", up, selected, fleet, force, ct).ConfigureAwait(false);
            return await RoleAsync(store, raising, selected, fleet, ct).ConfigureAwait(false);
        }

        // Mode off: the list is the one bound to the configuration, and the single-server fleet gives it all of it.
        var own = Unsplit;
        foreach (var name in up)
        {
            var listId = await RoutingBinding.ResolveAsync(store, name, ct).ConfigureAwait(false);
            var fleet = ServerFleet.Single(name);
            await ShareAsync(store, geo, name, [name], listId, fleet, force, ct).ConfigureAwait(false);
            if (string.Equals(name, raising, StringComparison.Ordinal))
            {
                own = await RoleAsync(store, raising, listId, fleet, ct).ConfigureAwait(false);
            }
        }

        return own;
    }

    // Reads what the place of the configuration being raised rests on: the list's own full-tunnel flag and the
    // configuration picked to carry everything. Neither is read while several servers work at once.
    private async Task<TunnelRole> RoleAsync(IStateStore store, string? raising, long? listId, ServerFleet fleet, CancellationToken ct)
    {
        if (raising is null)
        {
            return Unsplit;
        }

        if (fleet.MultiServer)
        {
            return Role(raising, listId, fleet, null, null);
        }

        var settings = listId is null ? null : await store.GetRoutingSettingsAsync(listId.Value, ct).ConfigureAwait(false);
        var picked = await store.GetSettingAsync(StateKeys.DefaultRouteOwner, ct).ConfigureAwait(false);
        return Role(raising, listId, fleet, settings, picked);
    }

    // Who the list is split between: the tunnels that are up, plus the one being raised, in priority order. The
    // tunnel actually carrying the default route heads the list, because that is where a rule addressing no server
    // rides; a configuration still dialling does not take that place from it.
    private IReadOnlyList<string> Up(IReadOnlyList<string> order, string? raising)
    {
        var up = order
            .Where(name => control.IsRunning(name) || string.Equals(name, raising, StringComparison.Ordinal))
            .ToList();
        if (control.DefaultRouteOwner is { Length: > 0 } holder && up.Remove(holder))
        {
            up.Insert(0, holder);
        }

        return up;
    }

    // Materializes the list against the fleet and writes each share; a tunnel that is up hears about its own.
    private async Task ShareAsync(IStateStore store, GeoConfigurator geo, string key, IReadOnlyList<string> servers, long? listId, ServerFleet fleet, bool force, CancellationToken ct)
    {
        var list = listId is null ? null : await store.GetRoutingListAsync(listId.Value, ct).ConfigureAwait(false);
        if (list is null)
        {
            _planned.Remove(key);
            foreach (var name in servers)
            {
                await HandOverAsync(name, store, Bare(name, fleet), ct).ConfigureAwait(false);
            }

            return;
        }

        var plan = RoutingPlan.Build(list.Rules, fleet);
        var mark = Mark(list, plan, fleet);
        if (!force && _planned.TryGetValue(key, out var last) && last == mark)
        {
            return;
        }

        _planned[key] = mark;
        var settings = await store.GetRoutingSettingsAsync(list.Id, ct).ConfigureAwait(false);
        var projection = await geo.ProjectAsync(list, plan, ct).ConfigureAwait(false);
        Journal(plan, fleet);
        foreach (var share in projection.Servers)
        {
            var split = Split(share.Server, fleet, settings);
            var carried = Fingerprint(list.Id, split, share.Routes, share.Domains, share.Apps, projection.BlockRoutes, projection.BlockDomains);
            if (Held(share.Server, carried))
            {
                continue;
            }

            await StoreFor(share.Server, store)
                .SaveTunnelProjectionAsync(share.Server, split, share.Routes, share.Domains, share.Apps, list.Id, projection.BlockRoutes, projection.BlockDomains, ct)
                .ConfigureAwait(false);
            _handed[share.Server] = carried;
            Announce(share.Server);
            if (split)
            {
                logger.LogInformation("{Server} carries {Routes} address range(s), {Domains} domain(s) and {Apps} application(s) of list '{List}'",
                    share.Server, share.Routes.Count, share.Domains.Count, share.Apps.Count, list.Name);
                continue;
            }

            logger.LogInformation("{Server} carries everything no other server is named for, plus {Routes} address range(s), {Domains} domain(s) and {Apps} application(s) of list '{List}'",
                share.Server, share.Routes.Count, share.Domains.Count, share.Apps.Count, list.Name);
        }
    }

    // A tunnel with no list carries everything, the way it does when the rules are off; beside the server that
    // carries everything it carries nothing instead, since no rule names it.
    private async Task HandOverAsync(string name, IStateStore fallback, bool bare, CancellationToken ct)
    {
        var carried = Fingerprint(null, bare, [], [], [], [], []);
        if (Held(name, carried))
        {
            return;
        }

        await StoreFor(name, fallback)
            .SaveTunnelProjectionAsync(name, bare, [], [], [], null, [], [], ct)
            .ConfigureAwait(false);
        _handed[name] = carried;
        Announce(name);
    }

    // The place a configuration takes on the machine.
    internal static TunnelRole Role(string config, long? listId, ServerFleet fleet, RoutingSettings? settings, string? picked)
    {
        if (fleet.MultiServer)
        {
            // The head of the fleet carries everything no rule sends elsewhere: with several servers the pick is
            // the priority order, not a switch, so the setting is not read at all.
            var head = string.Equals(fleet.First, config, StringComparison.Ordinal);
            return new TunnelRole(listId, !head, head);
        }

        // Mode off: the list says whether the tunnel carries everything, and the picked configuration takes the
        // default route from whoever holds it. With no list the tunnel carries everything, as it always has.
        return new TunnelRole(listId, listId is not null && Split(config, fleet, settings), string.Equals(picked, config, StringComparison.Ordinal));
    }

    // Whether a tunnel carries only what the list names it for. The head of the fleet carries everything besides;
    // with the mode off the list's own flag decides, the way it did before the mode existed.
    private static bool Split(string server, ServerFleet fleet, RoutingSettings? settings)
    {
        return fleet.MultiServer
            ? !string.Equals(server, fleet.First, StringComparison.Ordinal)
            : !(settings?.UseGlobalProxy ?? false);
    }

    // Whether a tunnel with no list of its own carries nothing at all: only a server beside the one that carries
    // everything is in that place.
    private static bool Bare(string server, ServerFleet fleet)
    {
        return fleet.MultiServer && !string.Equals(server, fleet.First, StringComparison.Ordinal);
    }

    private bool Held(string server, int carried)
    {
        return _handed.TryGetValue(server, out var last) && last == carried;
    }

    // The store the tunnel's own user owns; a tunnel raised by somebody else keeps its library.
    private IStateStore StoreFor(string server, IStateStore fallback)
    {
        return control.Find(server) is { } tunnel ? stores.For(tunnel.OwnerRoot) : fallback;
    }

    // A tunnel that went down is dropped, so it is written to again when it comes back.
    private void Forget(IReadOnlyList<string> up)
    {
        foreach (var gone in _handed.Keys.Where(name => !up.Contains(name, StringComparer.Ordinal)).ToList())
        {
            _handed.Remove(gone);
        }
    }

    // Says once what changed side, and warns about the two cases where a rule names something the library does not
    // answer to. With one server the verdicts say nothing worth reading: every rule rides the only tunnel there is.
    private void Journal(RoutingPlan plan, ServerFleet fleet)
    {
        if (!fleet.MultiServer)
        {
            return;
        }

        foreach (var verdict in plan.Verdicts)
        {
            var key = GeoConfigurator.FormatWithRole(verdict.Rule);
            if (_journalled.TryGetValue(key, out var last) && last == verdict.Target)
            {
                continue;
            }

            _journalled[key] = verdict.Target;
            if (verdict.Target.Unresolved)
            {
                logger.LogWarning("rule {Rule} names a configuration the library does not hold ({Reason}); until it is imported the rule rides whichever server carries the default route",
                    key, verdict.Target.Reason);
                continue;
            }

            logger.LogInformation("rule {Rule} now goes {Kind} {Server} ({Reason})", key, verdict.Target.Kind, verdict.Target.Server, verdict.Target.Reason);
        }
    }

    // The tunnel runs in its own process and polls for nothing: a share it has to redecide is announced to it.
    private void Announce(string server)
    {
        if (!control.IsRunning(server))
        {
            return;
        }

        _ = Task.Run(() => RuntimeSnapshotPipe.Send(server, RuntimeSnapshotPipe.OpRules, logger));
    }

    // Marks the plan: the rules as they stand, who is up and in what order, and where each rule came out. What the
    // rules expand into is not in it, which is what the forced pass after a geo refresh is for.
    private static int Mark(RoutingList list, RoutingPlan plan, ServerFleet fleet)
    {
        var hash = new HashCode();
        hash.Add(list.Id);
        foreach (var server in fleet.Up)
        {
            hash.Add(server);
        }

        foreach (var rule in list.Rules)
        {
            hash.Add(GeoConfigurator.FormatWithRole(rule));
        }

        foreach (var verdict in plan.Verdicts)
        {
            hash.Add(verdict.Target);
        }

        return hash.ToHashCode();
    }

    // Marks a share so one that did not move is not written and announced again.
    private static int Fingerprint(long? listId, bool split, IReadOnlyList<string> routes, IReadOnlyList<GeoDomain> domains, IReadOnlyList<string> apps, IReadOnlyList<string> blockRoutes, IReadOnlyList<GeoDomain> blockDomains)
    {
        var hash = new HashCode();
        hash.Add(listId);
        hash.Add(split);
        foreach (var route in routes)
        {
            hash.Add(route);
        }

        foreach (var domain in domains)
        {
            hash.Add(domain);
        }

        foreach (var app in apps)
        {
            hash.Add(app);
        }

        foreach (var route in blockRoutes)
        {
            hash.Add(route);
        }

        foreach (var domain in blockDomains)
        {
            hash.Add(domain);
        }

        return hash.ToHashCode();
    }
}
