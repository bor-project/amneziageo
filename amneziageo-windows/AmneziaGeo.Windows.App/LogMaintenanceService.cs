using AmneziaGeo.Dal;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Periodically prunes each log table to the retention cap so log.db stays bounded. Agent process only.
/// </summary>
internal sealed class LogMaintenanceService(SqliteLogStore store, LogSettings settings, AgentControl control, ILogger<LogMaintenanceService> logger) : BackgroundService
{
    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // One pass before the wait, so a log left oversized by a previous version shrinks without a connect.
            await PruneAsync(stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                // Prune only while a tunnel is up: log.db grows during a session; idle, little is written.
                await control.WaitUntilRunningAsync(stoppingToken);
                await PruneAsync(stoppingToken);
                await Task.Delay(LogRetention.Interval, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task PruneAsync(CancellationToken ct)
    {
        try
        {
            var pruned = await LogRetention.PruneAsync(store, settings.MaxRowsPerTable, ct);
            if (pruned.Total > 0)
            {
                logger.LogDebug("dropped the oldest {Agent} log entries, {Dns} resolver entries and {Routes} routing entries past the retention limit", pruned.Agent, pruned.Dns, pruned.Routes);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "old log entries could not be removed; the log file keeps growing until this succeeds");
        }
    }
}
