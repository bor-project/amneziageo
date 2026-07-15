using System.Net.NetworkInformation;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Wakes a stalled reconnect when the OS reports network connectivity changed.
/// </summary>
internal sealed class NetworkWatcher(AgentControl control, ILogger<NetworkWatcher> logger) : IHostedService
{
    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        NetworkChange.NetworkAvailabilityChanged += OnAvailabilityChanged;
        NetworkChange.NetworkAddressChanged += OnAddressChanged;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        NetworkChange.NetworkAvailabilityChanged -= OnAvailabilityChanged;
        NetworkChange.NetworkAddressChanged -= OnAddressChanged;
        return Task.CompletedTask;
    }

    private void OnAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (e.IsAvailable)
        {
            logger.LogDebug("network available; waking a stalled reconnect");
            control.WakeIfRetrying();
        }
    }

    private void OnAddressChanged(object? sender, EventArgs e)
    {
        control.WakeIfRetrying();
    }
}
