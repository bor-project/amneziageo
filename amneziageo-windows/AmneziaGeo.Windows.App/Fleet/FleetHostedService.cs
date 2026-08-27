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
    FleetRunnerFactory factory,
    AgentMode mode,
    AgentTarget target,
    AgentControl selected,
    SettingsStore settingsStore,
    NetworkReconciler reconciler,
    ActiveTunnelScope activeScope,
    ILogger<FleetHostedService> logger) : BackgroundService
{
    private readonly Dictionary<string, FleetMember> _members = new(StringComparer.Ordinal);

    // What the header last said, so only a move of it counts as a request.
    private string _lastTarget = string.Empty;
    private bool _lastRunning;

    // Whether a request has moved the set since it was restored: a start that raised nothing must not forget
    // what the mode last stood on.
    private bool _moved;

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

        await SeedSelectionAsync(stoppingToken);
        await RestoreAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            // Latched before the set is read, so a request that lands while it is being served is not lost. The
            // stop is one of the three: the wait ends with the supervisor, not only with a request.
            using (var change = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, fleet.ChangeToken, selected.ChangeToken))
            {
                Follow();
                await SyncAsync(stoppingToken);
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

        fleet.Restore(new FleetState(order, stored.Roles, stored.Primary, desired));
        if (desired.Length > 0)
        {
            logger.LogInformation("the set the mode last stood on is being connected: {Names}", string.Join(", ", desired));
        }

        Align(selected.Target ?? string.Empty);
        _lastTarget = selected.Target ?? string.Empty;
        _lastRunning = selected.Running;
    }

    // The set has no requests of its own until the mode's own operations land, so the selected configuration
    // speaks for it: connecting joins it to the set, disconnecting takes it out, and the rest of the set stands.
    // Only a move counts - the set the mode came back to is not re-asked on every wake.
    private void Follow()
    {
        var name = selected.Target ?? string.Empty;
        var running = selected.Running;
        var picked = !string.Equals(name, _lastTarget, StringComparison.Ordinal);
        if (!picked && running == _lastRunning)
        {
            return;
        }

        _lastTarget = name;
        _lastRunning = running;
        if (name.Length == 0)
        {
            return;
        }

        if (running)
        {
            if (fleet.Add(name))
            {
                _moved = true;
                logger.LogInformation("{Name}: asked for; {Count} tunnel(s) are now wanted up", name, fleet.Wanted.Count);
            }

            return;
        }

        // Another card was picked, not disconnected: the set stands as it is, and the header takes up the state
        // of the tunnel it now points at.
        if (picked)
        {
            Align(name);
            return;
        }

        if (fleet.Remove(name))
        {
            _moved = true;
            logger.LogInformation("{Name}: no longer asked for; {Count} tunnel(s) are now wanted up", name, fleet.Wanted.Count);
        }
    }

    // The header answers for the tunnel it points at, since that is what the mode reads requests off.
    private void Align(string name)
    {
        var up = name.Length > 0 && fleet.Wanted.Contains(name);
        if (up == selected.Running)
        {
            return;
        }

        selected.SetRunning(up);
        _lastRunning = up;
    }

    // Brings the running tunnels in line with the set.
    private async Task SyncAsync(CancellationToken ct)
    {
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

        // A tunnel reads its duties at bring-up, so one that has gained or lost the default route - the tunnel
        // ahead of it left the set - is dialled again to take them up.
        foreach (var member in _members.Values.ToArray())
        {
            if (fleet.For(member.Name) == member.Duties)
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
        if (!_moved)
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
        _members[name] = factory.Start(name, duties, ct);
        logger.LogInformation("{Name}: connecting as one of {Count} tunnel(s); it {Carries} what no rule sends elsewhere",
            name, _members.Count, duties.CarriesDefault ? "carries" : "does not carry");
    }

    private async Task StopAsync(string name)
    {
        if (!_members.Remove(name, out var member))
        {
            return;
        }

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
