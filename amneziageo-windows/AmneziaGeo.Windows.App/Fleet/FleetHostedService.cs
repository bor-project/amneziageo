using AmneziaGeo.Decl;
using AmneziaGeo.Ipc.Fleet;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App.Fleet;

/// <summary>
/// Drives the tunnels the machine keeps up at once: one supervisor each, raised and taken down as the set moves.
/// </summary>
internal sealed class FleetHostedService(
    FleetControl fleet,
    FleetStore fleetStore,
    FleetLive live,
    FleetRunnerFactory factory,
    AgentMode mode,
    AgentTarget target,
    AgentControl selected,
    SettingsStore settingsStore,
    NetworkReconciler reconciler,
    ActiveTunnelScope activeScope,
    ILogger<FleetHostedService> logger) : BackgroundService
{
    // How often the balancer is looked at again.
    private static readonly TimeSpan _balanceStep = TimeSpan.FromSeconds(30);

    private readonly Dictionary<string, FleetMember> _members = new(StringComparer.Ordinal);

    private IStateStore store => activeScope.Store;
    private ConfigRepository configRepo => activeScope.ConfigRepo;

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Nothing is connected yet, so every tunnel service left behind is a stray.
        var reaped = InstallerMaintenance.ReapTransientServices(null);
        if (reaped.Count > 0)
        {
            logger.LogInformation("removed {Count} tunnel(s) left behind by a previous run ({Names}); nothing was connected, so they were strays", reaped.Count, string.Join(", ", reaped));
        }

        // Stand the boot cleanup down the moment a tunnel is asked for: its own reconcile then owns adapter state.
        reconciler.Reconcile(WantsATunnel);
        _ = RetryBootReconcileAsync(stoppingToken);
        _ = BalanceAsync(stoppingToken);

        await SeedSelectionAsync(stoppingToken);
        await RestoreAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            // Latched before the set is read, so a request that lands while it is being served is not lost. The
            // stop is one of the three: the wait ends with the supervisor, not only with a request.
            using (var change = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, fleet.ChangeToken, selected.ChangeToken))
            {
                await SyncAsync(stoppingToken);
                Mirror();
                await PersistAsync(stoppingToken);
                await IdleAsync(change.Token);
            }
        }

        await StopAllAsync();
        logger.LogInformation("the background service is stopping; no tunnel is kept up while it is down");
    }

    // Brings the last owner's library forward and picks up the persisted selection, as the single-tunnel
    // supervisor does. What comes up is the mode's own business, so the selection only points the header.
    private async Task SeedSelectionAsync(CancellationToken ct)
    {
        var lastOwnerRoot = await store.GetSettingAsync("last-owner-root", ct);
        if (!string.IsNullOrEmpty(lastOwnerRoot) && !AppDataRoot.IsMachineRoot(lastOwnerRoot))
        {
            activeScope.SetOwner(lastOwnerRoot, null);
        }

        var stored = await store.GetSettingAsync(AgentControl.SelectedTargetKey, ct);
        var launch = !string.IsNullOrWhiteSpace(stored) ? stored! : target.Name;
        var config = !string.IsNullOrWhiteSpace(launch) && await configRepo.ExistsAsync(launch, ct)
            ? launch
            : string.Empty;
        if (config.Length == 0)
        {
            if (!string.IsNullOrWhiteSpace(stored))
            {
                await store.SetSettingAsync(AgentControl.SelectedTargetKey, string.Empty, ct);
            }

            logger.LogInformation("the background service is up with several servers on, but no configuration is selected; nothing will connect until you pick one");
            return;
        }

        logger.LogInformation("the background service is up with several servers on; configuration '{Config}' is selected", config);
        selected.SetTarget(config);

        if (string.IsNullOrWhiteSpace(stored))
        {
            await store.SetSettingAsync(AgentControl.SelectedTargetKey, config, ct);
        }
    }

    // The mode reads back its own state. The single-tunnel keys are neither read nor written, so switching the
    // mode off leaves the machine to stand back up on the tunnel it was on.
    private async Task RestoreAsync(CancellationToken ct)
    {
        var stored = await fleetStore.LoadAsync(ct);
        var library = await configRepo.ListAsync(ct);
        var known = new HashSet<string>(library, StringComparer.Ordinal);

        // A server deleted while the mode was off leaves nothing behind; the first time in, the servers are
        // listed as the library lists them.
        var order = stored.Order.Where(known.Contains).ToArray();
        if (order.Length == 0)
        {
            order = [.. library];
        }

        var desired = stored.Desired.Where(known.Contains).ToArray();
        var settings = await settingsStore.LoadAsync(ct);

        // A set is raised again on a start only when 'stay connected after a restart' allows it; the flag moving
        // under a running machine is a request of its own, and brings the set back either way.
        if (!mode.Switched && !settings.SurviveReboot && desired.Length > 0)
        {
            logger.LogInformation("the mode stands on {Count} tunnel(s) from last time, but 'stay connected after a restart' is off, so none of them is connected until you ask", desired.Length);
            desired = [];
        }

        fleet.Restore(new FleetState(order, stored.Roles, stored.Primary, desired, stored.Targets));
        if (desired.Length > 0)
        {
            logger.LogInformation("the set the mode last stood on is being connected: {Names}", string.Join(", ", desired));
        }

        Mirror();
    }

    // The machine's own lamp, which the window's push loop and the resolver watch read: in the mode it stands
    // for the set as a whole, and the snapshot answers for each server on its own.
    private void Mirror()
    {
        var up = fleet.Wanted.Count > 0;
        if (up != selected.Running)
        {
            selected.SetRunning(up);
        }
    }

    // Brings the running tunnels in line with the set.
    private async Task SyncAsync(CancellationToken ct)
    {
        foreach (var renamed in fleet.DrainRenames())
        {
            Follow(renamed.From, renamed.To);
        }

        var wanted = fleet.Wanted;
        foreach (var name in _members.Keys.Where(running => !wanted.Contains(running)).ToArray())
        {
            await StopAsync(name);
        }

        foreach (var name in wanted)
        {
            if (!_members.ContainsKey(name))
            {
                Start(name, ct);
            }
        }

        // A tunnel reads its duties and its share of the rules at bring-up, so one that has gained or lost the
        // default route - the tunnel ahead of it left the set - or whose rules were readdressed is dialled again.
        foreach (var member in _members.Values.ToArray())
        {
            if (fleet.For(member.Name) == member.Duties && fleet.Stamp == member.Stamp)
            {
                continue;
            }

            logger.LogInformation("{Name}: what it carries changed, so it is connected again to take it up", member.Name);
            await StopAsync(member.Name);
            Start(member.Name, ct);
        }
    }

    // The mode's own state is written once a request has moved the set: a start that did not raise what it
    // remembered must leave that memory where it is.
    private async Task PersistAsync(CancellationToken ct)
    {
        if (!fleet.Moved)
        {
            return;
        }

        try
        {
            await fleetStore.SaveAsync(fleet.Snapshot(), ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "the set could not be written down; it stands as asked and is written again on the next request");
        }
    }

    private void Start(string name, CancellationToken ct)
    {
        var duties = fleet.For(name);
        var member = factory.Start(name, duties, fleet.Stamp, ct);
        _members[name] = member;
        live.Publish(name, member.Control);
        logger.LogInformation("{Name}: connecting as one of {Count} tunnel(s); it {Carries} what no rule sends elsewhere",
            name, _members.Count, duties.CarriesDefault ? "carries" : "does not carry");
    }

    // Carries a renamed tunnel over: its supervisor resolves the configuration under the new name from here on.
    private void Follow(string oldName, string newName)
    {
        if (!_members.Remove(oldName, out var member))
        {
            return;
        }

        member.Control.RetargetName(oldName, newName);
        _members[newName] = new FleetMember(newName, member.Duties, member.Stamp, member.Control, member.Stop, member.Run);
        live.Retarget(oldName, newName);
        logger.LogInformation("{Old} is called '{New}' from now on; it stays up and carries what it carried", oldName, newName);
    }

    private async Task StopAsync(string name)
    {
        if (!_members.Remove(name, out var member))
        {
            return;
        }

        live.Drop(name);
        logger.LogInformation("{Name}: disconnecting; {Count} tunnel(s) left up", name, _members.Count);
        member.Control.SetRunning(false);
        await member.Stop.CancelAsync();
        try
        {
            await member.Run;
        }
        catch (OperationCanceledException)
        {
        }

        member.Stop.Dispose();
    }

    private async Task StopAllAsync()
    {
        foreach (var name in _members.Keys.ToArray())
        {
            await StopAsync(name);
        }

        live.Clear();
    }

    // Looks the balancer over on a timer, so a rule riding the quickest server follows the readings and goes
    // back to the primary the moment it answers again.
    private async Task BalanceAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_balanceStep, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (fleet.Rebalance(live.RoundTrips()))
            {
                logger.LogInformation("the balancer holds '{Name}' from now on; the tunnels take the rules riding it over again", fleet.Best);
            }
        }
    }

    // Retries the boot reconcile while nothing is asked for: each bring-up reconciles on its own.
    private async Task RetryBootReconcileAsync(CancellationToken ct)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(6), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (WantsATunnel())
            {
                return;
            }

            reconciler.Reconcile(WantsATunnel);
        }
    }

    private bool WantsATunnel()
    {
        return fleet.Wanted.Count > 0;
    }

    private static async Task IdleAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
