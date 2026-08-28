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
    private static readonly TimeSpan _initialDelay = TimeSpan.FromMinutes(3);

    // The timer only asks whose interval has run out; the interval itself is the panel's or the setting's.
    private static readonly TimeSpan _tick = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan _afterFailure = TimeSpan.FromHours(1);

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
            var delay = _tick;
            try
            {
                var settings = await settingsStore.LoadAsync(ct);
                if (settings.SubscriptionAutoRefresh)
                {
                    var refreshed = await broker.RefreshDueSubscriptionsAsync(settings.SubscriptionRefreshIntervalHours, ct);
                    if (refreshed > 0)
                    {
                        logger.LogInformation("re-read {Count} subscription(s); a rewritten configuration takes effect on the next connect", refreshed);
                    }
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
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
