using AmneziaGeo.Dal;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Periodically prunes each log table to the retention cap so log.db stays bounded. Agent process only.
/// </summary>
internal sealed class LogMaintenanceService(SqliteLogStore store, LogSettings settings, AgentControl control, ILogger<LogMaintenanceService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);

    // Diagnostic runs kept: each is a whole report, and a handful of them covers any support thread.
    private const int ChecksKept = 50;

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
                await Task.Delay(Interval, stoppingToken);
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
            var agent = await store.PruneAsync(SqliteLogStore.AgentTable, settings.MaxRowsPerTable, ct);
            var routes = await store.PruneAsync(SqliteLogStore.RoutesTable, settings.MaxRowsPerTable, ct);
            await store.PruneAsync(SqliteLogStore.ChecksTable, ChecksKept, ct);
            await store.PruneAsync(SqliteLogStore.ProbeTable, ChecksKept, ct);
            if (agent + routes > 0)
            {
                logger.LogDebug("dropped the oldest {Agent} log entries and {Routes} routing entries past the retention limit", agent, routes);
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
