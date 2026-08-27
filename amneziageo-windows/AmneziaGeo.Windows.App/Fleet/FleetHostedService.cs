using AmneziaGeo.Decl;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App.Fleet;

/// <summary>
/// Drives the tunnels the machine keeps up at once: one supervisor each, raised and taken down as the set moves.
/// </summary>
internal sealed class FleetHostedService(
    FleetControl fleet,
    FleetRunnerFactory factory,
    AgentTarget target,
    AgentControl selected,
    SettingsStore settingsStore,
    NetworkReconciler reconciler,
    ActiveTunnelScope activeScope,
    ILogger<FleetHostedService> logger) : BackgroundService
{
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

        await SeedSelectionAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            // Latched before the set is read, so a request that lands while it is being served is not lost.
            using (var change = CancellationTokenSource.CreateLinkedTokenSource(fleet.ChangeToken, selected.ChangeToken))
            {
                Follow();
                await SyncAsync(stoppingToken);
                await IdleAsync(change.Token);
            }
        }

        await StopAllAsync();
        logger.LogInformation("the background service is stopping; no tunnel is kept up while it is down");
    }

    // Brings the last owner's library forward and picks up the persisted selection, as the single-tunnel
    // supervisor does. A set that survives a restart is the mode's own state, and it is not stored yet.
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

        var settings = await settingsStore.LoadAsync(ct);
        if (settings.SurviveReboot)
        {
            logger.LogInformation("'stay connected after a restart' is on, so configuration '{Config}' is being connected without waiting for you", config);
            selected.SetRunning(true);
        }
    }

    // The set has no requests of its own until the mode's own operations land, so the selected configuration
    // speaks for it: connecting joins it to the set, disconnecting takes it out, and the rest of the set stands.
    private void Follow()
    {
        var name = selected.Target;
        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        if (selected.Running)
        {
            if (fleet.Add(name))
            {
                logger.LogInformation("{Name}: asked for; {Count} tunnel(s) are now wanted up", name, fleet.Wanted.Count);
            }

            return;
        }

        if (fleet.Remove(name))
        {
            logger.LogInformation("{Name}: no longer asked for; {Count} tunnel(s) are now wanted up", name, fleet.Wanted.Count);
        }
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
