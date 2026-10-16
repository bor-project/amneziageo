using System.Net.NetworkInformation;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Wakes a stalled reconnect when the underlay network changes.
/// </summary>
internal sealed class NetworkWatcher : IHostedService, IDisposable
{
    // Coalesces the burst of address changes one connectivity event produces.
    private static readonly TimeSpan _debounce = TimeSpan.FromSeconds(2);

    private readonly AgentControl _control;
    private readonly ILogger<NetworkWatcher> _logger;
    // Reads the interfaces of the moment into their signature.
    private readonly Func<string> _signature;
    private readonly Lock _gate = new();
    private Timer? _settle;
    private string _underlay = string.Empty;

    /// <summary>
    /// ctor
    /// </summary>
    public NetworkWatcher(AgentControl control, ILogger<NetworkWatcher> logger)
        : this(control, logger, UnderlaySignature)
    {
    }

    /// <summary>
    /// ctor
    /// </summary>
    internal NetworkWatcher(AgentControl control, ILogger<NetworkWatcher> logger, Func<string> signature)
    {
        _control = control;
        _logger = logger;
        _signature = signature;
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Listed again on the next change, which then counts as a real one; an exception escaping here fails the host.
        try
        {
            _underlay = _signature();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "the network interfaces could not be listed; the next network change wakes a reconnect waiting for the network, whatever changed");
        }

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
            _logger.LogWarning(ex, "this network change could not be examined; a reconnect waiting for the network back may start later than it could");
        }
    }

    // Wakes only on a real underlay change: raising and dropping our own tunnel adapter is itself an address
    // change, so an unfiltered wake lets every failed attempt trigger the next one (#206).
    private void WakeOnChange()
    {
        var signature = _signature();
        lock (_gate)
        {
            if (string.Equals(signature, _underlay, StringComparison.Ordinal))
            {
                return;
            }

            _underlay = signature;
        }

        _logger.LogDebug("underlay network changed; waking a stalled reconnect");
        _control.WakeIfRetrying();
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
