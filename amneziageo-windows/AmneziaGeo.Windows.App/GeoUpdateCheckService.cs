using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Periodically updates geo sources to their newest remote file.
/// </summary>
internal sealed class GeoUpdateCheckService(
    SettingsStore settingsStore,
    AgentStatusBroker broker,
    AgentControl control,
    ILogger<GeoUpdateCheckService> logger) : BackgroundService
{
    private static readonly TimeSpan _initialDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan _disabledPoll = TimeSpan.FromMinutes(30);
    private const int MinIntervalHours = 1;
    private const int MaxIntervalHours = 24 * 7;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(_initialDelay, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            // Skip geo checks without a tunnel: geo data only feeds an active tunnel's routing.
            try
            {
                await control.WaitUntilRunningAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            TimeSpan delay;
            try
            {
                var settings = await settingsStore.LoadAsync(ct);
                if (settings.GeoAutoCheck)
                {
                    var total = await broker.UpdateAllSourcesAsync(ct);
                    logger.LogInformation("geo auto-update: refreshing {Total} source(s)", total);
                    // Return the materialization transient before the long sleep.
                    MemoryReclaim.Trim();
                    delay = TimeSpan.FromHours(Math.Clamp(settings.GeoCheckIntervalHours, MinIntervalHours, MaxIntervalHours));
                }
                else
                {
                    delay = _disabledPoll;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "geo auto-check failed");
                delay = TimeSpan.FromHours(1);
            }

            try
            {
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
