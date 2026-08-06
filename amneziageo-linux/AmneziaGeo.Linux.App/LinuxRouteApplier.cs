using System.Net;
using AmneziaGeo.Linux.Engine;
using AmneziaGeo.Routing;

namespace AmneziaGeo.Linux.App;

/// <summary>
/// Carries out the routing verdicts with iproute2 and the engine's control channel: a host route through the
/// physical hop, a host route into the tunnel with its advertisement, or a blackhole for a blocked destination.
/// </summary>
internal sealed class LinuxRouteApplier : IRouteApplier
{
    // One generation for the whole session: nothing here rearms a filter set, so an installed route never goes stale.
    private const int Session = 1;

    private readonly string _iface;
    private readonly string? _peerKey;
    private readonly AwgDaemon _daemon;
    private readonly string? _gateway;
    private readonly string? _device;
    private readonly IReadOnlyList<string> _advertised;
    private readonly string? _endpoint;
    private readonly AgentLog _log;
    private readonly HashSet<string> _live = new(StringComparer.Ordinal);
    private readonly object _sync = new();
    private int _endpointWarned;

    /// <summary>
    /// ctor
    /// </summary>
    public LinuxRouteApplier(string interfaceName, string? peerPublicKeyHex, AwgDaemon daemon, string? gateway, string? device, IReadOnlyList<string> advertised, string? endpoint, AgentLog log)
    {
        _iface = interfaceName;
        _peerKey = peerPublicKeyHex;
        _daemon = daemon;
        _gateway = gateway;
        _device = device;
        _advertised = advertised;
        _endpoint = endpoint;
        _log = log;
    }

    /// <inheritdoc/>
    public int Generation => Session;

    /// <summary>
    /// Permits one host address through the physical path; nothing to install, that path carries no kill-switch.
    /// </summary>
    public bool TryPermit(uint address, out ulong outId, out ulong inId, out int generation)
    {
        outId = 0;
        inId = 0;
        generation = Session;
        return true;
    }

    /// <summary>
    /// Blackholes one host address. The route is its own filter, so the address is the id it is deleted by.
    /// </summary>
    public bool TryDrop(uint address, out ulong outId, out ulong inId, out int generation)
    {
        outId = address;
        inId = 0;
        generation = Session;
        var host = $"{GeoIpRanges.Format(address)}/32";
        if (_endpoint is not null && host == $"{_endpoint}/32")
        {
            outId = 0;
            if (Interlocked.Exchange(ref _endpointWarned, 1) == 0)
            {
                _log.Warn("route", $"the block list covers the server itself at {_endpoint}, which stays reachable: blocking it would take the tunnel down with it");
            }

            return true;
        }

        return Ip("route", "replace", "blackhole", host);
    }

    /// <summary>
    /// Adds a host route out the physical hop.
    /// </summary>
    public bool TryAddRoute(IPAddress address, out uint interfaceIndex)
    {
        interfaceIndex = 0;
        return _gateway is not null && _device is not null
            && Ip("route", "replace", Cidr(address), "via", _gateway, "dev", _device);
    }

    /// <summary>
    /// Removes a host route.
    /// </summary>
    public void RemoveRoute(IPAddress address, uint interfaceIndex)
    {
        Ip("route", "del", Cidr(address));
    }

    /// <summary>
    /// Routes one address into the tunnel. The advertisement goes first: the engine drops what the peer does not
    /// carry, so a route laid before it would lose the packets that earned it.
    /// </summary>
    public bool TryTunnel(IPAddress address)
    {
        var host = Cidr(address);
        if (!Advertise(host))
        {
            return false;
        }

        if (Ip("route", "replace", host, "dev", _iface))
        {
            return true;
        }

        Withdraw([host]);
        return false;
    }

    /// <summary>
    /// Withdraws tunnelled addresses: their routes go first, so the traffic falls back to the physical path before
    /// the peer stops carrying them.
    /// </summary>
    public void RemoveTunnel(IReadOnlyCollection<IPAddress> addresses)
    {
        if (addresses.Count == 0)
        {
            return;
        }

        var hosts = new List<string>(addresses.Count);
        foreach (var address in addresses)
        {
            var host = Cidr(address);
            hosts.Add(host);
            Ip("route", "del", host, "dev", _iface);
        }

        Withdraw(hosts);
    }

    /// <summary>
    /// Deletes the blackhole routes of the addresses given.
    /// </summary>
    public void DeleteFilters(IReadOnlyList<(ulong Out, ulong In)> filters, int generation)
    {
        foreach (var (blackhole, _) in filters)
        {
            if (blackhole != 0)
            {
                Ip("route", "del", "blackhole", $"{GeoIpRanges.Format((uint)blackhole)}/32");
            }
        }
    }

    // Hands one range to the peer and remembers it, so a later withdrawal can rebuild what the engine carries.
    private bool Advertise(string cidr)
    {
        if (_peerKey is null)
        {
            return false;
        }

        lock (_sync)
        {
            if (!_live.Add(cidr))
            {
                return true;
            }

            try
            {
                _daemon.AddAllowedIpAsync(_peerKey, cidr).GetAwaiter().GetResult();
                return true;
            }
            catch (Exception ex)
            {
                _live.Remove(cidr);
                _log.Error("route", $"advertising {cidr} to the engine failed", ex);
                return false;
            }
        }
    }

    // Rewrites what the peer carries with the ranges the tunnel came up with plus the addresses still held: the
    // control channel takes a whole set, it has no way to withdraw one range.
    private void Withdraw(IReadOnlyCollection<string> cidrs)
    {
        if (_peerKey is null)
        {
            return;
        }

        lock (_sync)
        {
            var held = false;
            foreach (var cidr in cidrs)
            {
                held |= _live.Remove(cidr);
            }

            if (!held)
            {
                return;
            }

            try
            {
                _daemon.ReplaceAllowedIpsAsync(_peerKey, [.. _advertised, .. _live]).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _log.Error("route", "withdrawing addresses from the engine failed", ex);
            }
        }
    }

    private bool Ip(params string[] args)
    {
        var (exitCode, output) = Shell.RunAsync("ip", CancellationToken.None, args).GetAwaiter().GetResult();
        if (exitCode != 0)
        {
            _log.Warn("route", $"ip {string.Join(' ', args)} failed: {output}");
            return false;
        }

        _log.Route(string.Join(' ', args));
        return true;
    }

    private static string Cidr(IPAddress address) =>
        address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"{address}/128" : $"{address}/32";
}
