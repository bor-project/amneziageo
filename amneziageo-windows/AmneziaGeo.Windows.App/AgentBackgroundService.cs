using AmneziaGeo.Decl;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Hosted service that drives the balancer orchestrator for the agent's active group.
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
        // Heal any DNS/route state a crashed or severed predecessor left behind before doing anything.
        reconciler.Reconcile();

        // Prefer the persisted user selection (survives restarts) over the launch argument; the launch
        // arg is only a seed for the preconfigured installer's "main". On a clean install both are empty,
        // so the service serves the pipe and idles — no phantom binding — until the GUI creates a profile
        // and selects it. A persisted selection that no longer resolves is a broken binding: drop it.
        var stored = await store.GetSettingAsync(AgentControl.SelectedTargetKey, stoppingToken);
        var launch = !string.IsNullOrWhiteSpace(stored) ? stored! : target.Name;
        var group = string.IsNullOrWhiteSpace(launch) ? null : await ResolveGroupAsync(launch, stoppingToken);
        if (group is not null)
        {
            logger.LogInformation("agent starting: profile {Profile} (config '{Config}')", group.Name, group.Config);
            control.SetTarget(group.Name);

            // Persist a launch-arg seed (preconfigured "--agent main") as the selection so it sticks even
            // if the argument is later dropped.
            if (string.IsNullOrWhiteSpace(stored))
            {
                await store.SetSettingAsync(AgentControl.SelectedTargetKey, group.Name, stoppingToken);
            }
        }
        else
        {
            // Clear a dangling persisted selection so the connection card shows a clean slate, not a
            // phantom target that can never connect.
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

        if (configRepo.Exists(target))
        {
            return new BalancerGroup(target, target);
        }

        return null;
    }
}
