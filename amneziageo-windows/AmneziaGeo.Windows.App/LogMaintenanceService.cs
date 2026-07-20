using AmneziaGeo.Dal;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Periodically prunes each log table to the retention cap so log.db stays bounded. Agent process only.
/// </summary>
internal sealed class LogMaintenanceService(SqliteLogStore store, LogSettings settings, ILogger<LogMaintenanceService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await PruneAsync(stoppingToken);
            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task PruneAsync(CancellationToken ct)
    {
        try
        {
            var agent = await store.PruneAsync(SqliteLogStore.AgentTable, settings.MaxRowsPerTable, ct);
            var routes = await store.PruneAsync(SqliteLogStore.RoutesTable, settings.MaxRowsPerTable, ct);
            if (agent + routes > 0)
            {
                logger.LogDebug("log retention: pruned {Agent} agent, {Routes} route rows", agent, routes);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "log retention prune failed");
        }
    }
}
