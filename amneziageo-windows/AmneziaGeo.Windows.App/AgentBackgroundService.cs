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
    IHostApplicationLifetime lifetime,
    ILogger<AgentBackgroundService> logger) : BackgroundService
{
    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var group = await ResolveGroupAsync(target.Name, stoppingToken);
        if (group is null)
        {
            logger.LogError("agent: unknown target {Target}", target.Name);
            lifetime.StopApplication();
            return;
        }

        logger.LogInformation("agent starting: group {Group} ({Count} member(s))", group.Name, group.Members.Count);
        await runner.RunAsync(group, stoppingToken);
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
