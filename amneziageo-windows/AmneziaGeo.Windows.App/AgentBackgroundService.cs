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

        // The launch target may not exist yet (fresh install: no profiles configured). Don't abort the
        // service — serve the pipe and idle so the GUI can connect, create a profile, then select +
        // connect. Only seed the selected target when it actually resolves; otherwise leave it unset and
        // hand the runner an empty group to idle on.
        var group = await ResolveGroupAsync(target.Name, stoppingToken);
        if (group is not null)
        {
            logger.LogInformation("agent starting: group {Group} ({Count} member(s))", group.Name, group.Members.Count);
            control.SetTarget(group.Name);
        }
        else
        {
            logger.LogInformation("agent starting: target '{Target}' not configured yet; idling", target.Name);
        }

        await runner.RunAsync(group ?? new BalancerGroup(target.Name, 60, []), stoppingToken);
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
            return new BalancerGroup(target, 60, [target]);
        }

        return null;
    }
}
