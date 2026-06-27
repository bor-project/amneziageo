using AmneziaGeo.Decl;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// On agent startup, seeds the default geo sources for a fresh install (empty source list) so the
/// source list is never empty out of the box. The actual download of the geo data is driven by the
/// installer (a checkbox → the synchronous "download-geo" IPC op), or by the user in the app, not here.
/// </summary>
internal sealed class GeoBootstrapService(
    IStateStore store,
    AgentStatusBroker broker,
    ILogger<GeoBootstrapService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            if (await GeoDefaults.SeedIfEmptyAsync(store, logger, ct))
            {
                await broker.BroadcastIfChangedAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "geo seed failed");
        }
    }
}
