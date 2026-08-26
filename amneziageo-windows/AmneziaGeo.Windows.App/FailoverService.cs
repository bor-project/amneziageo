using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using AmneziaGeo.Ipc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Carries the default route off a server that stopped answering and onto the next one in the priority list, and
/// back to a server standing higher once it answers again and the tunnel falls quiet. The decision belongs to
/// <see cref="FailoverPolicy"/>; this reads the tunnels, hands it what it sees and does what it asks for.
/// </summary>
internal sealed class FailoverService(
    AgentControl control,
    SettingsStore settingsStore,
    ScopedStoreFactory stores,
    ServiceManager serviceManager,
    AgentStatusBroker broker,
    RoutingDistributor distributor,
    ILogger<FailoverService> logger) : BackgroundService
{
    // Rounds are folded at the pace the tunnels are read at: nothing fresher than the liveness poll gets here.
    private static readonly TimeSpan _interval = TimeSpan.FromSeconds(5);

    private readonly FailoverPolicy _policy = new();

    // Servers the route has been handed to since one last carried it. Without them the walk keeps to the head of
    // the list and a server further down is never dialled.
    private readonly List<string> _dialled = [];

    // Servers dialled by auto-switching itself, and taken down by it alone: a tunnel the user raised by hand is
    // none of its business.
    private readonly HashSet<string> _reserved = new(StringComparer.Ordinal);

    // Servers already reported as standing in the addresses of a tunnel that is up, so it is said once and not
    // every round for as long as both configurations are there.
    private readonly HashSet<string> _collided = new(StringComparer.Ordinal);

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_interval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                await RoundAsync(ct).ConfigureAwait(false);

                // A tunnel that came up or went down changes who carries what: the round is when that is recounted.
                if (control.Primary is { } primary)
                {
                    await distributor.DistributeAsync(primary.OwnerRoot, ct: ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // One failed round is not a reason to leave the servers unwatched for the rest of the session.
                logger.LogDebug(ex, "the auto-switching round could not run");
            }
        }
    }

    // Folds one round of readings and does what the policy asks for.
    private async Task RoundAsync(CancellationToken ct)
    {
        // Auto-switching moves the default route, so it has nothing to say while no tunnel carries it.
        if (control.Find(control.DefaultRouteOwner) is not { } holder)
        {
            return;
        }

        if (holder.HandshakeAge >= 0)
        {
            // A server that answers ends the walk that was looking for one.
            _dialled.Clear();
        }
        else if (holder.RetryAttempt == 0)
        {
            // Inside its first dial: the connect budget is not spent yet, so the round says nothing either way.
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var app = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var settings = new FailoverSettings(app.FailoverEnabled, app.FailoverReturnMinutes);
        var store = stores.For(holder.OwnerRoot);
        if (!settings.Enabled)
        {
            // Switched off: the policy forgets the streaks, so switching it back on starts from a clean slate.
            _policy.Decide([], settings, holder.Config, now);
            await ReserveAsync(store, [], holder, ct).ConfigureAwait(false);
            return;
        }

        var order = await new ConfigRepository(store, serviceManager).ListAsync(ct).ConfigureAwait(false);
        var participants = FailoverPolicy.Participants(order, app.FailoverSkipped);
        var decision = _policy.Decide([.. participants.Select(Read)], settings, holder.Config, now);
        var reserves = FailoverPolicy.Reserves(participants, holder.Config, holder.HandshakeAge >= 0, settings);
        await ReserveAsync(store, reserves, holder, ct).ConfigureAwait(false);
        if (decision.Action == FailoverAction.Stay)
        {
            return;
        }

        var next = decision.Config;
        if (decision.Action == FailoverAction.Switch)
        {
            next = FailoverPolicy.Walk(participants, holder.Config, next, _dialled);
            if (next.Length == 0)
            {
                // Every participant has been dialled since the route was carried; the walk starts another lap.
                _dialled.Clear();
                next = decision.Config;
            }
        }

        await MoveAsync(store, participants, holder, next, decision.Action, settings, ct).ConfigureAwait(false);
    }

    // What one server looks like from here. A tunnel whose peer has answered speaks through its counters; one
    // that is down says nothing either way.
    private FailoverReading Read(string name)
    {
        return control.Find(name) is { Running: true, HandshakeAge: >= 0 } tunnel
            ? new FailoverReading(name, true, tunnel.Link)
            : new FailoverReading(name, false, LinkReading.Empty);
    }

    // Keeps beside the tunnel the servers the route may go back to, and takes down the ones it may not. A reserve
    // carries nothing: the tunnel holding the default route keeps it, and the reserve is raised with only the
    // ranges it names, which for a server carrying everything is none. Its keepalive is what asks the peer
    // whether it is there.
    private async Task ReserveAsync(IStateStore store, IReadOnlyList<string> wanted, TunnelControl holder, CancellationToken ct)
    {
        foreach (var name in wanted)
        {
            // A configuration that already failed to dial is left alone: raising it every round would walk into
            // the same failure for as long as the tunnel stands.
            if (control.Find(name) is { Running: true } or { ConnectFailed: true })
            {
                continue;
            }

            if (await CollidesAsync(store, name, ct).ConfigureAwait(false))
            {
                continue;
            }

            control.For(name, holder.OwnerRoot, holder.OwnerSid).SetRunning(true);
            _reserved.Add(name);
            logger.LogInformation("{Config} stands higher in the auto-switching list, so it is dialled and kept beside the tunnel carrying nothing, ready for everything to go back to it", name);
        }

        foreach (var name in _reserved.Where(name => !wanted.Contains(name, StringComparer.Ordinal)).ToList())
        {
            _reserved.Remove(name);
            if (control.Find(name) is { Running: true } reserve)
            {
                reserve.SetRunning(false);
                logger.LogInformation("{Config} is no longer a server everything can go back to, so it is taken down", name);
            }
        }

        _collided.RemoveWhere(name => !wanted.Contains(name, StringComparer.Ordinal));
    }

    // Whether a server would stand in the addresses of a tunnel that is already up. Two servers handing out one
    // subnet answer to the same address, so an echo meant for one of them measures the other and the reserve
    // would be read by the wrong channel. Standing it down is what keeps the readings honest.
    private async Task<bool> CollidesAsync(IStateStore store, string name, CancellationToken ct)
    {
        var addresses = await AddressesAsync(store, name, ct).ConfigureAwait(false);
        foreach (var tunnel in control.Desired)
        {
            if (!tunnel.Running || string.Equals(tunnel.Config, name, StringComparison.Ordinal))
            {
                continue;
            }

            if (!TunnelOverlap.Same(addresses, await AddressesAsync(store, tunnel.Config, ct).ConfigureAwait(false)))
            {
                continue;
            }

            if (_collided.Add(name))
            {
                logger.LogWarning("{Config} stands in the same addresses as {Other}, which is up, so it is not kept beside it: an echo to one of them would measure the other. Give one of the two servers a subnet of its own to have everything go back to it", name, tunnel.Config);
            }

            return true;
        }

        return false;
    }

    // The addresses a configuration puts on its interface.
    private static async Task<IReadOnlyList<string>> AddressesAsync(IStateStore store, string name, CancellationToken ct)
    {
        var text = await store.GetConfigTextAsync(name, ct).ConfigureAwait(false) ?? string.Empty;
        return WgConfigEditor.GetAddresses(text);
    }

    // Hands the default route over: the pick is written, the server that had it goes down unless the route may
    // still come back to it, and the one taking its place is dialled. A server already up as a reserve is dialled
    // again by the same call, and this time it is the pick, so it takes the route from whoever holds it.
    private async Task MoveAsync(
        IStateStore store,
        IReadOnlyList<string> participants,
        TunnelControl holder,
        string next,
        FailoverAction action,
        FailoverSettings settings,
        CancellationToken ct)
    {
        await store.SetSettingAsync(StateKeys.DefaultRouteOwner, next, ct).ConfigureAwait(false);
        var handed = await HandOverAsync(store, holder, next, ct).ConfigureAwait(false);
        var kept = settings.ReturnMinutes > 0
            && FailoverPolicy.Above(participants, next).Contains(holder.Config, StringComparer.Ordinal);
        if (kept)
        {
            _reserved.Add(holder.Config);
        }
        else
        {
            _reserved.Remove(holder.Config);
            holder.SetRunning(false);
        }

        _reserved.Remove(next);
        if (!handed)
        {
            control.For(next, holder.OwnerRoot, holder.OwnerSid).SetRunning(true);
        }

        _dialled.Add(next);

        // A reserve stands only while the tunnel it waits beside does: what comes back after a restart is the
        // tunnel alone.
        var desired = control.Desired.Where(tunnel => !_reserved.Contains(tunnel.Config)).Select(tunnel => tunnel.Config);
        await store.SetSettingAsync(StateKeys.DesiredTunnels, string.Join(Environment.NewLine, desired), ct).ConfigureAwait(false);

        if (action == FailoverAction.Return)
        {
            logger.LogInformation("{Config} stands higher in the auto-switching list and answers again, so everything goes back to it while the tunnel is quiet; {Other} is taken down", next, holder.Config);
        }
        else if (kept)
        {
            logger.LogWarning("{Config} stopped answering, so everything moves to {Other}; the one that fell keeps standing beside it, ready to be gone back to", holder.Config, next);
        }
        else
        {
            logger.LogWarning("{Config} stopped answering, so it is taken down and everything moves to {Other}", holder.Config, next);
        }

        await broker.BroadcastIfChangedAsync(ct).ConfigureAwait(false);
    }

    // Moves the route onto a server that is already standing beside the tunnel, which takes it over without being
    // dialled: what is open through it stays open, and the move costs a pipe round-trip instead of a handshake.
    // Only a tunnel raised with the default route clipped off it is asked - that is a tunnel that wanted to carry
    // everything and was refused, and nothing else is owed the route.
    private async Task<bool> HandOverAsync(IStateStore store, TunnelControl holder, string next, CancellationToken ct)
    {
        if (control.Find(next) is not { Running: true, HandshakeAge: >= 0 })
        {
            return false;
        }

        if (await store.GetSettingAsync(TunnelPaths.DefaultRouteKey(next), ct).ConfigureAwait(false) != TunnelPaths.ClipDefaultRoute)
        {
            return false;
        }

        // Make before break: the one taking over carries everything before the one giving up stops, so no packet
        // meets a moment with nowhere to go.
        if (RuntimeSnapshotPipe.Send(next, RuntimeSnapshotPipe.Carry(take: true), logger) != "ok")
        {
            logger.LogInformation("{Config} could not take everything over as it stands, so it is dialled again to carry it", next);
            return false;
        }

        RuntimeSnapshotPipe.Send(holder.Config, RuntimeSnapshotPipe.Carry(take: false), logger);
        control.ClaimDefaultRoute(next, preferred: true);
        control.ClaimResolver(next);
        logger.LogInformation("{Config} was already standing beside the tunnel, so it took everything over without being dialled and what was open through it stayed open", next);
        return true;
    }
}
