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
    IGeoFileStore geoFiles,
    AgentStatusBroker broker,
    ILogger<GeoBootstrapService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            var seeded = await GeoDefaults.SeedIfEmptyAsync(store, logger, ct);
            var rebuilt = await new GeoConfigurator(store, geoFiles).RematerializeIfStaleAsync(ct);
            if (rebuilt)
            {
                logger.LogInformation("the rule expansion changed, the stored routing lists were rebuilt against it");
            }

            if (seeded || rebuilt)
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
