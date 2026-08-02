using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
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
            logger.LogWarning(ex, "the standard rule databases could not be registered on first start; rules by country or by service match nothing until they are added and downloaded");
        }
    }
}
