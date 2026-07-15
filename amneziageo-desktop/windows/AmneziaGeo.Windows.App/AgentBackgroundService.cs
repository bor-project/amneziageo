using AmneziaGeo.Decl;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Drives the profile runner for the agent's active profile.
/// </summary>
internal sealed class AgentBackgroundService(
    AgentTarget target,
    IStateStore store,
    ConfigRepository configRepo,
    ProfileRunner runner,
    AgentControl control,
    SettingsStore settingsStore,
    NetworkReconciler reconciler,
    ILogger<AgentBackgroundService> logger) : BackgroundService
{
    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Reap orphaned tunnel services before reconcile: nothing is connected yet, so any leftover is a stray (#168).
        var reaped = InstallerMaintenance.ReapTransientServices(null);
        if (reaped.Count > 0)
        {
            logger.LogInformation("reaped {Count} orphaned tunnel service(s): {Names}", reaped.Count, string.Join(", ", reaped));
        }

        reconciler.Reconcile();

        // Persisted selection wins over the launch arg; a dangling selection is dropped.
        var stored = await store.GetSettingAsync(AgentControl.SelectedTargetKey, stoppingToken);
        var launch = !string.IsNullOrWhiteSpace(stored) ? stored! : target.Name;
        var group = string.IsNullOrWhiteSpace(launch) ? null : await ResolveProfileAsync(launch, stoppingToken);
        if (group is not null)
        {
            logger.LogInformation("agent starting: profile {Profile} (config '{Config}')", group.Name, group.Config);
            control.SetTarget(group.Name);

            if (string.IsNullOrWhiteSpace(stored))
            {
                await store.SetSettingAsync(AgentControl.SelectedTargetKey, group.Name, stoppingToken);
            }

            // Survive-reboot: dial the selected profile on start; the supervisor's retry engine waits out a
            // missing network (backoff + a NetworkWatcher wake) until the host answers or rejects the config.
            var settings = await settingsStore.LoadAsync(stoppingToken);
            if (settings.SurviveReboot)
            {
                logger.LogInformation("survive-reboot on: auto-connecting profile {Profile}", group.Name);
                control.SetRunning(true);
            }
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(stored))
            {
                await store.SetSettingAsync(AgentControl.SelectedTargetKey, string.Empty, stoppingToken);
            }

            logger.LogInformation("agent starting: no target configured yet; idling");
        }

        await runner.RunAsync(group ?? new Profile(string.Empty, string.Empty), stoppingToken);
        logger.LogInformation("agent stopped");
    }

    private async Task<Profile?> ResolveProfileAsync(string target, CancellationToken ct)
    {
        var profile = await store.GetProfileAsync(target, ct);
        if (profile is not null)
        {
            return profile;
        }

        if (await configRepo.ExistsAsync(target, ct))
        {
            return new Profile(target, target);
        }

        return null;
    }
}
