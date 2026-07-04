using AmneziaGeo.Decl;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Drives the balancer orchestrator for the agent's active group.
/// </summary>
internal sealed class AgentBackgroundService(
    AgentTarget target,
    IStateStore store,
    ConfigRepository configRepo,
    BalancerRunner runner,
    AgentControl control,
    NetworkReconciler reconciler,
    ILogger<AgentBackgroundService> logger) : BackgroundService
{
    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        reconciler.Reconcile();

        // Persisted selection wins over the launch arg; a dangling selection is dropped.
        var stored = await store.GetSettingAsync(AgentControl.SelectedTargetKey, stoppingToken);
        var launch = !string.IsNullOrWhiteSpace(stored) ? stored! : target.Name;
        var group = string.IsNullOrWhiteSpace(launch) ? null : await ResolveGroupAsync(launch, stoppingToken);
        if (group is not null)
        {
            logger.LogInformation("agent starting: profile {Profile} (config '{Config}')", group.Name, group.Config);
            control.SetTarget(group.Name);

            if (string.IsNullOrWhiteSpace(stored))
            {
                await store.SetSettingAsync(AgentControl.SelectedTargetKey, group.Name, stoppingToken);
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

        await runner.RunAsync(group ?? new BalancerGroup(string.Empty, string.Empty), stoppingToken);
        logger.LogInformation("agent stopped");
    }

    private async Task<BalancerGroup?> ResolveGroupAsync(string target, CancellationToken ct)
    {
        var balancer = await store.GetBalancerAsync(target, ct);
        if (balancer is not null)
        {
            return balancer;
        }

        if (await configRepo.ExistsAsync(target, ct))
        {
            return new BalancerGroup(target, target);
        }

        return null;
    }
}
