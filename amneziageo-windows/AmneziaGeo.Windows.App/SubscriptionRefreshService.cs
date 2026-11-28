using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Re-reads the subscriptions whose interval has run out. A subscription lives on the open internet,
/// so this runs whether or not a tunnel is up.
/// </summary>
internal sealed class SubscriptionRefreshService(
    SettingsStore settingsStore,
    AgentStatusBroker broker,
    ILogger<SubscriptionRefreshService> logger) : BackgroundService
{
    // The timer only asks whose interval has run out; the interval itself is the panel's or the setting's.
    private static readonly TimeSpan _tick = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan _afterFailure = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var initial = true;
        while (!ct.IsCancellationRequested)
        {
            var delay = _tick;
            try
            {
                var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
                if (settings.SubscriptionAutoRefresh)
                {
                    await broker.AutoRefreshConfigurationsAsync(settings.SubscriptionRefreshIntervalHours, initial, ct).ConfigureAwait(false);
                    initial = false;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "the subscriptions could not be re-read; the configurations already there keep working, next attempt in an hour");
                delay = _afterFailure;
            }

            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
