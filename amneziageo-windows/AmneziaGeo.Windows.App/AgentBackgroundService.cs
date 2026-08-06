using AmneziaGeo.Decl;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Drives the config runner for the agent's selected config.
/// </summary>
internal sealed class AgentBackgroundService(
    AgentTarget target,
    ConfigRunner runner,
    AgentControl control,
    SettingsStore settingsStore,
    NetworkReconciler reconciler,
    ActiveTunnelScope activeScope,
    ILogger<AgentBackgroundService> logger) : BackgroundService
{
    private IStateStore store => activeScope.Store;
    private ConfigRepository configRepo => activeScope.ConfigRepo;

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
            activeScope.SetOwner(lastOwnerRoot, null);
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

            // Survive-reboot: dial the selected config on start; the supervisor's retry engine waits out a
            // missing network (backoff + a NetworkWatcher wake) until the host answers or rejects the config.
            var settings = await settingsStore.LoadAsync(stoppingToken);
            if (settings.SurviveReboot)
            {
                logger.LogInformation("'stay connected after a restart' is on, so configuration '{Config}' is being connected without waiting for you", config);
                control.SetRunning(true);
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

        await runner.RunAsync(config, stoppingToken);
        logger.LogInformation("the background service is stopping; no tunnel is kept up while it is down");
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
}
