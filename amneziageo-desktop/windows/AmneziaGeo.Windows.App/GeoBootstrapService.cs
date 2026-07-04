using AmneziaGeo.Decl;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Seeds default geo sources on agent startup for a fresh install.
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
