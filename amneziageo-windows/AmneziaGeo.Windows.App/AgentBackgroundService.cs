using AmneziaGeo.Decl;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Keeps a runner for every tunnel the agent is asked to raise, and reaps it once the tunnel is torn down.
/// </summary>
internal sealed class AgentBackgroundService(
    AgentTarget target,
    ConfigRunnerFactory runners,
    AgentControl control,
    SettingsStore settingsStore,
    NetworkReconciler reconciler,
    ScopedStoreFactory stores,
    ServiceManager serviceManager,
    ILogger<AgentBackgroundService> logger) : BackgroundService
{
    // Budget the runners get to tear their tunnels down while the agent shuts down.
    private static readonly TimeSpan _shutdownTimeout = TimeSpan.FromSeconds(30);

    private string _bootRoot = AppDataRoot.Base();

    private IStateStore store => stores.For(_bootRoot);
    private ConfigRepository configRepo => new(store, serviceManager);

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Reap orphaned tunnel services before reconcile: nothing is connected yet, so any leftover is a stray (#168).
        var reaped = InstallerMaintenance.ReapTransientServices(null);
        if (reaped.Count > 0)
        {
            logger.LogInformation("removed {Count} tunnel(s) left behind by a previous run ({Names}); nothing was connected, so they were strays", reaped.Count, string.Join(", ", reaped));
        }

        // Stand the boot cleanup down the moment a connect is requested: its own reconcile then owns adapter state.
        reconciler.Reconcile(() => control.Running);

        // A boot-time DNS restore can fail to take while WMI or the adapter is still initializing; re-run it a
        // few times over the first minute so a leaked loopback redirect cannot strand the box until a manual connect.
        _ = RetryBootReconcileAsync(stoppingToken);

        // Bring the last active user's library forward so boot auto-connect uses their config; a machine root left
        // by an older build is ignored, it holds no library.
        var lastOwnerRoot = await store.GetSettingAsync("last-owner-root", stoppingToken);
        if (!string.IsNullOrEmpty(lastOwnerRoot) && !AppDataRoot.IsMachineRoot(lastOwnerRoot))
        {
            _bootRoot = lastOwnerRoot;
        }

        // Persisted selection wins over the launch arg; a dangling selection is dropped.
        var stored = await store.GetSettingAsync(AgentControl.SelectedTargetKey, stoppingToken);
        var launch = !string.IsNullOrWhiteSpace(stored) ? stored! : target.Name;
        var config = !string.IsNullOrWhiteSpace(launch) && await configRepo.ExistsAsync(launch, stoppingToken)
            ? launch
            : string.Empty;
        if (config.Length > 0)
        {
            logger.LogInformation("the background service is up; configuration '{Config}' is selected", config);
            control.SetTarget(config);

            if (string.IsNullOrWhiteSpace(stored))
            {
                await store.SetSettingAsync(AgentControl.SelectedTargetKey, config, stoppingToken);
            }
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(stored))
            {
                await store.SetSettingAsync(AgentControl.SelectedTargetKey, string.Empty, stoppingToken);
            }

            logger.LogInformation("the background service is up, but no configuration is selected; nothing will connect until you pick one");
        }

        // Survive-reboot: dial every tunnel that was up on the way down; the retry engine waits out a missing
        // network (backoff + a NetworkWatcher wake) until the host answers or rejects the config.
        var settings = await settingsStore.LoadAsync(stoppingToken);
        if (settings.SurviveReboot)
        {
            await RaiseSurvivorsAsync(config, stoppingToken);
        }

        await SuperviseAsync(stoppingToken);
        logger.LogInformation("the background service is stopping; no tunnel is kept up while it is down");
    }

    // Brings back the tunnels that were up before the restart, falling back to the selected one for a machine
    // that predates the stored set.
    private async Task RaiseSurvivorsAsync(string selected, CancellationToken ct)
    {
        var stored = await store.GetSettingAsync(StateKeys.DesiredTunnels, ct) ?? string.Empty;
        var names = stored
            .Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (names.Count == 0 && selected.Length > 0)
        {
            names.Add(selected);
        }

        foreach (var name in names)
        {
            if (!await configRepo.ExistsAsync(name, ct))
            {
                logger.LogInformation("configuration '{Config}' was up before the restart but is no longer in the library, so it is not dialled", name);
                continue;
            }

            logger.LogInformation("'stay connected after a restart' is on, so configuration '{Config}' is being connected without waiting for you", name);
            control.For(name, _bootRoot).SetRunning(true);
        }
    }

    // Starts a runner for every tunnel that has become desired and drops the ones that have finished tearing down.
    private async Task SuperviseAsync(CancellationToken ct)
    {
        var live = new Dictionary<TunnelControl, RunnerHandle>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var membership = control.MembershipToken;

                // Reaped before the spawn: a tunnel raised again while its last runner was finishing must find
                // its slot free, or it waits for a signal that has already been spent.
                foreach (var (tunnel, handle) in live.Where(entry => entry.Value.Task.IsCompleted).ToList())
                {
                    live.Remove(tunnel);
                    handle.Cancellation.Dispose();
                    control.Forget(tunnel.Config);
                    logger.LogDebug("the tunnel of {Config} is down; its supervisor is gone", tunnel.Config);
                }

                foreach (var tunnel in control.Desired)
                {
                    if (live.ContainsKey(tunnel))
                    {
                        continue;
                    }

                    var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var runner = runners.Create(tunnel);
                    live[tunnel] = new RunnerHandle(Task.Run(() => runner.RunAsync(cts.Token), CancellationToken.None), cts);
                    logger.LogDebug("a supervisor is now watching the tunnel of {Config}", tunnel.Config);
                }

                await IdleAsync(membership, ct);
            }
        }
        finally
        {
            await DrainAsync(live);
        }
    }

    // Lets every runner finish its teardown before the agent goes down; one that hangs is left to the service
    // control manager rather than holding the stop open.
    private async Task DrainAsync(Dictionary<TunnelControl, RunnerHandle> live)
    {
        if (live.Count == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(live.Values.Select(handle => handle.Task)).WaitAsync(_shutdownTimeout);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "one of the tunnels did not finish tearing down while the agent was stopping");
        }

        foreach (var handle in live.Values)
        {
            handle.Cancellation.Dispose();
        }
    }

    private static async Task IdleAsync(CancellationToken membership, CancellationToken ct)
    {
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, membership))
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, linked.Token);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    // Retries the boot reconcile while no tunnel is desired: RestoreSaved is a no-op once the leftover DNS state
    // is cleared, and the loop stops once a connection is requested (that path reconciles on its own).
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

            if (control.Running)
            {
                return;
            }

            reconciler.Reconcile(() => control.Running);
        }
    }

    // A running supervisor and the token that ends it.
    private sealed record RunnerHandle(Task Task, CancellationTokenSource Cancellation);
}
