using System.Net.NetworkInformation;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Wakes a stalled reconnect when the underlay network changes.
/// </summary>
internal sealed class NetworkWatcher(AgentControl control, ILogger<NetworkWatcher> logger) : IHostedService, IDisposable
{
    // Coalesces the burst of address changes one connectivity event produces.
    private static readonly TimeSpan _debounce = TimeSpan.FromSeconds(2);

    private readonly Lock _gate = new();
    private Timer? _settle;
    private string _underlay = string.Empty;

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _underlay = UnderlaySignature();
        _settle = new Timer(OnSettled, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        NetworkChange.NetworkAvailabilityChanged += OnAvailabilityChanged;
        NetworkChange.NetworkAddressChanged += OnAddressChanged;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        NetworkChange.NetworkAvailabilityChanged -= OnAvailabilityChanged;
        NetworkChange.NetworkAddressChanged -= OnAddressChanged;
        lock (_gate)
        {
            _settle?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_gate)
        {
            _settle?.Dispose();
            _settle = null;
        }
    }

    private void OnAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        Schedule();
    }

    private void OnAddressChanged(object? sender, EventArgs e)
    {
        Schedule();
    }

    private void Schedule()
    {
        lock (_gate)
        {
            _settle?.Change(_debounce, Timeout.InfiniteTimeSpan);
        }
    }

    // Fault-isolate the timer thread: an escaped exception here takes the agent process down.
    private void OnSettled(object? state)
    {
        try
        {
            WakeOnChange();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "underlay check failed");
        }
    }

    // Wakes only on a real underlay change: raising and dropping our own tunnel adapter is itself an address
    // change, so an unfiltered wake lets every failed attempt trigger the next one (#206).
    private void WakeOnChange()
    {
        var signature = UnderlaySignature();
        lock (_gate)
        {
            if (string.Equals(signature, _underlay, StringComparison.Ordinal))
            {
                return;
            }

            _underlay = signature;
        }

        logger.LogDebug("underlay network changed; waking a stalled reconnect");
        control.WakeIfRetrying();
    }

    // Operational non-tunnel interfaces with their addresses.
    private static string UnderlaySignature()
    {
        var parts = new List<string>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up
                || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback
                || RouteManager.IsTunnelAdapter(ni))
            {
                continue;
            }

            parts.AddRange(AddressesOf(ni));
        }

        parts.Sort(StringComparer.Ordinal);
        return string.Join("|", parts);
    }

    // Link-local v6 rotates on its own and would fake a change.
    private static IReadOnlyList<string> AddressesOf(NetworkInterface ni)
    {
        try
        {
            var result = new List<string>();
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (!ua.Address.IsIPv6LinkLocal)
                {
                    result.Add($"{ni.Id}:{ua.Address}");
                }
            }

            return result;
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }
}
