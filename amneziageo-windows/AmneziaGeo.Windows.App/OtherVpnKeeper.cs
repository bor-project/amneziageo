using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Keeps the VPNs of other programs working beside one tunnel: their adapters pass the leak protection, the ranges
/// they lead stay with them, and their servers are reached past the tunnel. Where this tunnel carries the default,
/// a VPN whose default it covers gets the network of its own address, as Windows gives it without a default through it.
/// </summary>
internal sealed class OtherVpnKeeper(string name, RouteManager routes, WindowsFirewall firewall, ILogger logger, bool carriesDefault)
{
    // How long the adapters settle after a change before they are read again.
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(2);

    // How long after a VPN came or went its routes are looked at once more: Windows adds the routes a server pushes
    // a few seconds after the link is up.
    private static readonly TimeSpan SecondLook = TimeSpan.FromSeconds(6);

    // How long the bring-up waits for the names of the servers.
    private static readonly TimeSpan ResolveBudget = TimeSpan.FromSeconds(3);

    private readonly object _gate = new();
    private IReadOnlyList<OtherVpns.Adapter> _adapters = [];
    private IReadOnlyList<IPAddress> _servers = [];
    // The host routes to the servers this keeper put, by address and adapter.
    private readonly Dictionary<IPAddress, uint> _laid = [];
    // The networks of the addresses of the other VPNs this keeper routed through them, by adapter.
    private readonly Dictionary<uint, (IPAddress Network, byte Prefix)> _classes = [];
    // The address the physical hop is read toward once the tunnel holds the default.
    private IPAddress? _probe;

    /// <summary>
    /// Called after the adapters of the other VPNs came or went and were squared with the tunnel.
    /// </summary>
    public Action? Changed { get; set; }

    /// <summary>
    /// Whether the adapter belongs to another VPN.
    /// </summary>
    public bool Leads(uint interfaceIndex)
    {
        lock (_gate)
        {
            return _adapters.Any(adapter => adapter.Index == interfaceIndex);
        }
    }

    /// <summary>
    /// The adapters of the other VPNs, by index.
    /// </summary>
    public IReadOnlyList<uint> Adapters()
    {
        lock (_gate)
        {
            return [.. _adapters.Select(adapter => adapter.Index)];
        }
    }

    /// <summary>
    /// Reads the adapters and the servers of the connections, waiting a short while for the names.
    /// </summary>
    public async Task ScanAsync(CancellationToken ct)
    {
        routes.ForgetAdapterKinds();
        var adapters = OtherVpns.Adapters();
        lock (_gate)
        {
            _adapters = adapters;
        }

        foreach (var adapter in adapters)
        {
            logger.LogInformation("{Name}: another VPN stands on {Adapter} ({Kind}); its adapter passes the leak protection and the ranges it leads stay with it", name, adapter.Name, adapter.Kind);
        }

        var resolving = ResolveAsync(OtherVpns.Servers(), ct);
        if (await Task.WhenAny(resolving, Task.Delay(ResolveBudget, ct)).ConfigureAwait(false) != resolving)
        {
            logger.LogDebug("{Name}: the servers of the VPN connections are still being looked up; the tunnel comes up without waiting for them", name);
            _ = resolving.ContinueWith(_ => Square(), ct, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
        }
    }

    /// <summary>
    /// Names the address the physical hop is read toward while the tunnel holds the default.
    /// </summary>
    public void Probe(IPAddress? probe)
    {
        _probe = probe;
    }

    /// <summary>
    /// Lays the routes to the servers past the tunnel, lets the adapters and the servers through the leak protection
    /// and gives back the ranges another VPN leads itself, as they all stand now.
    /// </summary>
    public void Square()
    {
        IReadOnlyList<OtherVpns.Adapter> adapters;
        IReadOnlyList<IPAddress> servers;
        lock (_gate)
        {
            adapters = _adapters;
            servers = _servers;
        }

        try
        {
            var (opened, closed) = firewall.PermitOtherVpns([.. adapters.Select(adapter => adapter.Index)]);
            var (reached, dropped) = firewall.PermitOtherServers([.. servers.Select(Numeric)]);
            var laid = LayRoutes(servers);
            var released = routes.ReleaseLanExclusions(name, [.. adapters.Select(adapter => adapter.Index)]);
            var classed = LayClassRoutes(adapters);
            if (opened + closed + reached + dropped + laid + released + classed > 0)
            {
                logger.LogInformation("{Name}: other VPNs squared: {Opened} adapter(s) let through the leak protection and {Closed} no longer, {Reached} server(s) reachable past the tunnel and {Dropped} no longer, {Laid} route(s) to servers laid, {Released} range(s) given back to them, {Classed} network(s) of their addresses sent through them",
                    name, opened, closed, reached, dropped, laid, released, classed);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "{Name}: the other VPNs could not be squared with the tunnel; the one that changed may stay blocked until the next connect", name);
        }
    }

    /// <summary>
    /// Follows the adapters: a VPN that comes or goes is squared with the tunnel after it settles.
    /// </summary>
    public async Task FollowAsync(CancellationToken ct)
    {
        var changed = new SemaphoreSlim(0, 1);
        void OnChanged(object? sender, EventArgs e)
        {
            try
            {
                changed.Release();
            }
            catch (SemaphoreFullException)
            {
            }
        }

        NetworkChange.NetworkAddressChanged += OnChanged;
        try
        {
            while (true)
            {
                await changed.WaitAsync(ct).ConfigureAwait(false);
                do
                {
                    await Task.Delay(SettleDelay, ct).ConfigureAwait(false);
                }
                while (changed.Wait(0));

                if (await RescanAsync(ct).ConfigureAwait(false))
                {
                    await Task.Delay(SecondLook, ct).ConfigureAwait(false);
                    await RescanAsync(ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            NetworkChange.NetworkAddressChanged -= OnChanged;
        }
    }

    /// <summary>
    /// Takes back the routes this keeper put: to the servers and through the other VPNs.
    /// </summary>
    public void Release()
    {
        KeyValuePair<IPAddress, uint>[] laid;
        KeyValuePair<uint, (IPAddress Network, byte Prefix)>[] classes;
        lock (_gate)
        {
            laid = [.. _laid];
            _laid.Clear();
            classes = [.. _classes];
            _classes.Clear();
        }

        foreach (var (address, index) in laid)
        {
            routes.RemoveOtherServerRoute(address, index);
        }

        foreach (var (index, (network, prefix)) in classes)
        {
            routes.RemoveOnLinkRange(network, prefix, index);
        }
    }

    // Sends the network of the address of every other VPN whose default route this tunnel covers through that VPN,
    // unless it leads the network itself; returns how many networks were sent.
    private int LayClassRoutes(IReadOnlyList<OtherVpns.Adapter> adapters)
    {
        if (!carriesDefault)
        {
            return 0;
        }

        var sent = 0;
        lock (_gate)
        {
            foreach (var (index, (network, prefix)) in _classes.Where(pair => adapters.All(adapter => adapter.Index != pair.Key)).ToArray())
            {
                routes.RemoveOnLinkRange(network, prefix, index);
                _classes.Remove(index);
            }

            foreach (var adapter in adapters)
            {
                if (_classes.ContainsKey(adapter.Index) || adapter.Address is not { } address || !RouteManager.HoldsDefault(adapter.Index))
                {
                    continue;
                }

                var (network, prefix) = ClassOf(address);
                if (RouteManager.LeadsRange(adapter.Index, network, prefix) || !routes.AddOnLinkRange(network, prefix, adapter.Index))
                {
                    continue;
                }

                _classes[adapter.Index] = (network, prefix);
                sent++;
                logger.LogInformation("{Name}: the VPN on {Adapter} holds a default route this tunnel covers, so the network of its address, {Network}/{Prefix}, goes through it, as Windows sends it without a default through a VPN",
                    name, adapter.Name, network, prefix);
            }
        }

        return sent;
    }

    // The network of the address by its class: /8 under 128, /16 under 192, /24 above.
    internal static (IPAddress Network, byte Prefix) ClassOf(IPAddress address)
    {
        var octets = address.GetAddressBytes();
        var prefix = octets[0] < 128 ? (byte)8 : octets[0] < 192 ? (byte)16 : (byte)24;
        for (var i = prefix / 8; i < 4; i++)
        {
            octets[i] = 0;
        }

        return (new IPAddress(octets), prefix);
    }

    // Reads the adapters again and squares what came and went; returns whether a VPN came or went.
    private async Task<bool> RescanAsync(CancellationToken ct)
    {
        try
        {
            routes.ForgetAdapterKinds();
            var adapters = OtherVpns.Adapters();
            IReadOnlyList<OtherVpns.Adapter> before;
            lock (_gate)
            {
                before = _adapters;
                _adapters = adapters;
            }

            foreach (var adapter in adapters.Where(adapter => before.All(one => one.Index != adapter.Index)))
            {
                logger.LogInformation("{Name}: another VPN came up on {Adapter} ({Kind}); its adapter passes the leak protection and the ranges it leads stay with it", name, adapter.Name, adapter.Kind);
            }

            foreach (var adapter in before.Where(adapter => adapters.All(one => one.Index != adapter.Index)))
            {
                logger.LogInformation("{Name}: the VPN on {Adapter} ({Kind}) went down", name, adapter.Name, adapter.Kind);
            }

            await ResolveAsync(OtherVpns.Servers(), ct).ConfigureAwait(false);
            Square();
            var moved = adapters.Count != before.Count || adapters.Any(adapter => before.All(one => one.Index != adapter.Index));
            if (moved || adapters.Count > 0)
            {
                Changed?.Invoke();
            }

            return moved;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "{Name}: the adapters of other VPNs could not be read after a change, so the ones read before stand", name);
            return false;
        }
    }

    // Looks the servers up and keeps their addresses.
    private async Task ResolveAsync(IReadOnlyList<string> servers, CancellationToken ct)
    {
        var addresses = await OtherVpns.ResolveAsync(servers, ct).ConfigureAwait(false);
        lock (_gate)
        {
            _servers = addresses;
        }
    }

    // Lays a host route past the tunnel to every server without one and takes back the routes of servers that are
    // gone; returns how many were laid.
    private int LayRoutes(IReadOnlyList<IPAddress> servers)
    {
        var laid = 0;
        lock (_gate)
        {
            foreach (var (address, index) in _laid.Where(pair => !servers.Contains(pair.Key)).ToArray())
            {
                routes.RemoveOtherServerRoute(address, index);
                _laid.Remove(address);
            }

            foreach (var address in servers.Where(address => !_laid.ContainsKey(address)))
            {
                // A server the machine has a path of its own to, on a connected network or by a route of the VPN, keeps it.
                if (routes.OwnPath(address) is not null)
                {
                    continue;
                }

                var (gateway, index) = RouteManager.UnderlayHop(_probe ?? address);
                if (index == 0 || routes.IsOwnAdapter(index))
                {
                    continue;
                }

                if (routes.AddOtherServerRoute(address, gateway, index))
                {
                    _laid[address] = index;
                    laid++;
                }
            }
        }

        return laid;
    }

    private static uint Numeric(IPAddress address)
    {
        return BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());
    }
}
