using System.Net;
using AmneziaGeo.Decl;
using AmneziaGeo.Linux.Engine;
using AmneziaGeo.Routing;

namespace AmneziaGeo.Linux.App;

/// <summary>
/// Carries out the routing verdicts with iproute2 and the engine's control channel: a host route through the
/// physical hop, a host route into the tunnel with its advertisement, or a blackhole for a blocked destination.
/// </summary>
internal sealed class LinuxRouteApplier : IRouteApplier
{
    private readonly string _iface;
    private readonly string? _peerKey;
    private readonly AwgDaemon _daemon;
    private readonly string? _gateway;
    private readonly string? _device;
    private readonly List<string> _advertised;
    private readonly string? _endpoint;
    private readonly AgentLog _log;
    private readonly HashSet<string> _live = new(StringComparer.Ordinal);
    // The held addresses the mark of their range carries, which hold no route of their own.
    private readonly HashSet<uint> _marked = [];
    // The held addresses a route out the physical hop was laid for.
    private readonly HashSet<uint> _laid = [];
    // The held addresses a direct range of the list leads past the tunnel, which hold no route of their own.
    private readonly HashSet<uint> _passed = [];
    private readonly object _sync = new();
    private AppTunnel? _apps;
    private Func<uint, RouteVerdict>? _verdicts;
    private int _generation = 1;
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
        _advertised = [.. advertised];
        _endpoint = endpoint;
        _log = log;
    }

    /// <inheritdoc/>
    public int Generation => Volatile.Read(ref _generation);

    /// <summary>
    /// Tells that the ranges of the list were laid anew, so every permit is to be asked for again.
    /// </summary>
    public void Rearm() => Interlocked.Increment(ref _generation);

    /// <summary>
    /// A blackhole is a route of the main table and a spared address an element of the application set: a rearm
    /// leaves both in place, so they are deleted whatever generation laid them.
    /// </summary>
    public bool FiltersOutliveRearm => true;

    /// <summary>
    /// Makes the peer carry every destination. The carried applications leave through the tunnel without asking the
    /// cache first, so the engine has to accept what it never advertised for them.
    /// </summary>
    public bool CarryEverything() => Advertise("0.0.0.0/0");

    /// <summary>
    /// Hands over the per-application path, so a decided destination is steered for the carried applications too.
    /// </summary>
    public void Attach(AppTunnel? apps)
    {
        _apps = apps;
    }

    /// <summary>
    /// Hands over the verdicts of the cache, which tell an address a rule keeps direct from one no rule names.
    /// </summary>
    public void Follow(Func<uint, RouteVerdict> verdicts)
    {
        _verdicts = verdicts;
    }

    /// <summary>
    /// Permits one host address through the physical path; nothing to install, that path carries no kill-switch.
    /// </summary>
    public bool TryPermit(uint address, out ulong outId, out ulong inId, out int generation)
    {
        outId = 0;
        inId = 0;
        generation = Generation;
        // The carried applications default into the tunnel, so an address a rule kept direct needs a route of its
        // own there.
        _apps?.Steer(address, $"{GeoIpRanges.Format(address)}/32", _gateway, _device, false);
        // A range of the list, an application or every datagram would carry what a rule kept off the tunnel.
        if (_apps?.Spare(address, _verdicts?.Invoke(address) == RouteVerdict.Direct) == true)
        {
            inId = address;
        }

        return true;
    }

    /// <summary>
    /// Blackholes one host address. The route is its own filter, so the address is the id it is deleted by.
    /// </summary>
    public bool TryDrop(uint address, out ulong outId, out ulong inId, out int generation)
    {
        outId = address;
        inId = 0;
        generation = Generation;
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

        _apps?.Steer(address, host, null, null, true);
        return Ip("route", "replace", "blackhole", host);
    }

    /// <summary>
    /// Adds a host route out the physical hop, unless the machine already leads the address past the tunnel.
    /// </summary>
    public bool TryAddRoute(IPAddress address, out uint interfaceIndex)
    {
        interfaceIndex = 0;
        var numeric = AppTunnel.Numeric(address);
        var host = Cidr(address);
        _apps?.Steer(numeric, host, _gateway, _device, false);
        lock (_laid)
        {
            if (Leading(address) is { } table)
            {
                var ranged = table == DirectPath.Table;
                if (ranged)
                {
                    _passed.Add(numeric);
                }

                _log.Route($"{host} goes past {_iface} by {(ranged ? "a direct range of the list" : "the routes of the machine")}");
                return true;
            }

            if (_gateway is null || _device is null || !Ip("route", "replace", host, "via", _gateway, "dev", _device))
            {
                return false;
            }

            _laid.Add(numeric);
            return true;
        }
    }

    /// <summary>
    /// Removes the host route laid for an address.
    /// </summary>
    public void RemoveRoute(IPAddress address, uint interfaceIndex)
    {
        var numeric = AppTunnel.Numeric(address);
        var host = Cidr(address);
        _apps?.Unsteer(host);
        lock (_laid)
        {
            _passed.Remove(numeric);
            if (_laid.Remove(numeric))
            {
                Ip("route", "del", host);
            }
        }
    }

    /// <summary>
    /// Lays a route out the physical hop for every held address a direct range led past the tunnel and none of the
    /// ranges given does.
    /// </summary>
    public void Uphold(IReadOnlyList<string> ranges)
    {
        lock (_laid)
        {
            if (_passed.Count == 0 || _gateway is null || _device is null)
            {
                return;
            }

            var standing = GeoIpRanges.Build(ranges);
            var left = _passed.Where(address => !standing.Contains(address)).ToList();
            if (left.Count == 0)
            {
                return;
            }

            var hosts = left.Select(address => $"{GeoIpRanges.Format(address)}/32").ToList();
            if (!Batch(SteeringRules.Bypasses(hosts, _gateway, _device, SteeringRules.MainTable), $"routing {left.Count} address(es) out {_device}"))
            {
                return;
            }

            _passed.ExceptWith(left);
            _laid.UnionWith(left);
            foreach (var host in hosts)
            {
                _log.Route($"route replace {host} via {_gateway} dev {_device}");
            }

            _log.Info("routing", $"{left.Count} address(es) keep their way past {_iface} by a route of their own: the direct ranges standing in the routes no longer cover them");
        }
    }

    /// <summary>
    /// Routes one address into the tunnel. The advertisement goes first: the engine drops what the peer does not
    /// carry, so a route laid before it would lose the packets that earned it. An address the mark of its range
    /// carries takes no route: one would send the answers to a connection that came in beside the tunnel into it.
    /// </summary>
    public bool TryTunnel(IPAddress address)
    {
        var host = Cidr(address);
        if (!Advertise(host))
        {
            return false;
        }

        // The tunnel is where the carried applications go by default, so a route that held this address outside it
        // has to give way.
        _apps?.Unsteer(host);

        if (Mark(address))
        {
            _log.Route($"{host} rides {_iface} by the mark of its range");
            return true;
        }

        if (Ip("route", "replace", host, "dev", _iface))
        {
            return true;
        }

        Withdraw([host]);
        return false;
    }

    /// <summary>
    /// Routes addresses into the tunnel in one batch: the peer takes the whole set in a single request and
    /// iproute2 the whole set in a single run.
    /// </summary>
    public IReadOnlyList<IPAddress> AddTunnel(IReadOnlyList<IPAddress> addresses)
    {
        if (addresses.Count == 0 || _peerKey is null)
        {
            return [];
        }

        var cidrs = new List<string>(addresses.Count);
        foreach (var address in addresses)
        {
            cidrs.Add(Cidr(address));
        }

        if (!AdvertiseMany(cidrs))
        {
            return [];
        }

        foreach (var cidr in cidrs)
        {
            _apps?.Unsteer(cidr);
        }

        var routed = new List<string>(addresses.Count);
        foreach (var address in addresses)
        {
            if (!Mark(address))
            {
                routed.Add(Cidr(address));
            }
        }

        if (routed.Count > 0 && !RouteMany(routed))
        {
            foreach (var address in addresses)
            {
                Unmark(address);
            }

            Withdraw(cidrs);
            return [];
        }

        if (routed.Count < addresses.Count)
        {
            _log.Route($"{addresses.Count - routed.Count} address(es) ride {_iface} by the mark of their range");
        }

        return addresses;
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
            _apps?.Unsteer(host);
            if (!Unmark(address))
            {
                Ip("route", "del", host, "dev", _iface);
            }
        }

        Withdraw(hosts);
    }

    /// <summary>
    /// Lays a route of its own for every held address the mark is about to stop carrying, before the ranges it
    /// goes by are replaced with the ones given.
    /// </summary>
    public void Shore(IReadOnlyList<string> ranges)
    {
        foreach (var address in Left(GeoIpRanges.Build(ranges)))
        {
            if (Ip("route", "replace", $"{GeoIpRanges.Format(address)}/32", "dev", _iface))
            {
                lock (_sync)
                {
                    _marked.Remove(address);
                }
            }
        }
    }

    /// <summary>
    /// Squares the ranges the tunnel keeps standing with an edited list and returns what the peer carries from the
    /// start: a gone range loses its route before its advertisement, a new one is advertised before its route.
    /// </summary>
    public IReadOnlyList<string> Restand(IReadOnlyList<string> added, IReadOnlyList<string> removed)
    {
        foreach (var cidr in removed)
        {
            Ip("route", "del", cidr, "dev", _iface);
        }

        var carried = Readvertise(added, removed);
        foreach (var cidr in added)
        {
            Ip("route", "replace", cidr, "dev", _iface);
        }

        return carried;
    }

    // Rewrites the standing ranges and hands the peer the whole set; returns the standing ranges.
    private IReadOnlyList<string> Readvertise(IReadOnlyList<string> added, IReadOnlyList<string> removed)
    {
        lock (_sync)
        {
            _advertised.RemoveAll(cidr => removed.Contains(cidr, StringComparer.Ordinal));
            foreach (var cidr in added)
            {
                if (!_advertised.Contains(cidr, StringComparer.Ordinal))
                {
                    _advertised.Add(cidr);
                }
            }

            if (_peerKey is not null)
            {
                try
                {
                    _daemon.ReplaceAllowedIpsAsync(_peerKey, [.. _advertised, .. _live]).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    _log.Error("route", "handing the standing ranges to the engine failed", ex);
                }
            }

            return [.. _advertised];
        }
    }

    /// <summary>
    /// Deletes the blackhole routes of the addresses given and hands the spared ones back to their ranges.
    /// </summary>
    public void DeleteFilters(IReadOnlyList<(ulong Out, ulong In)> filters, int generation)
    {
        foreach (var (blackhole, spared) in filters)
        {
            if (blackhole != 0)
            {
                _apps?.Unsteer($"{GeoIpRanges.Format((uint)blackhole)}/32");
                Ip("route", "del", "blackhole", $"{GeoIpRanges.Format((uint)blackhole)}/32");
            }

            if (spared != 0)
            {
                _apps?.Unspare((uint)spared);
            }
        }
    }

    // The routing table that leads the address anywhere but into the tunnel; null when the tunnel takes it.
    private string? Leading(IPAddress address)
    {
        var (exitCode, output) = Shell.RunAsync("ip", CancellationToken.None, "route", "get", address.ToString()).GetAwaiter().GetResult();
        return exitCode == 0 ? SteeringRules.Leading(output, _iface) : null;
    }

    // The held addresses the mark carries now and the ranges will not.
    private uint[] Left(GeoIpRanges carried)
    {
        lock (_sync)
        {
            return [.. _marked.Where(address => !carried.Contains(address))];
        }
    }

    // Remembers an address the mark of its range carries; false when no mark carries it.
    private bool Mark(IPAddress address)
    {
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || _apps is not { } apps)
        {
            return false;
        }

        var numeric = AppTunnel.Numeric(address);
        if (!apps.Carries(numeric))
        {
            return false;
        }

        lock (_sync)
        {
            _marked.Add(numeric);
        }

        return true;
    }

    // Forgets an address the mark carried; false when it held a route of its own.
    private bool Unmark(IPAddress address)
    {
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return false;
        }

        lock (_sync)
        {
            return _marked.Remove(AppTunnel.Numeric(address));
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

    // Hands the peer the whole set at once: the control channel takes a set, not an addition per range.
    private bool AdvertiseMany(IReadOnlyList<string> cidrs)
    {
        if (_peerKey is null)
        {
            return false;
        }

        lock (_sync)
        {
            var added = new List<string>(cidrs.Count);
            foreach (var cidr in cidrs)
            {
                if (_live.Add(cidr))
                {
                    added.Add(cidr);
                }
            }

            if (added.Count == 0)
            {
                return true;
            }

            try
            {
                _daemon.ReplaceAllowedIpsAsync(_peerKey, [.. _advertised, .. _live]).GetAwaiter().GetResult();
                return true;
            }
            catch (Exception ex)
            {
                foreach (var cidr in added)
                {
                    _live.Remove(cidr);
                }

                _log.Error("route", $"advertising {added.Count} address(es) to the engine failed", ex);
                return false;
            }
        }
    }

    // Adds the tunnel routes in one iproute2 run.
    private bool RouteMany(IReadOnlyList<string> cidrs)
    {
        if (!Batch(string.Concat(cidrs.Select(cidr => $"route replace {cidr} dev {_iface}\n")), $"routing {cidrs.Count} address(es) into {_iface}"))
        {
            return false;
        }

        _log.Route($"{cidrs.Count} address(es) into {_iface}");
        return true;
    }

    // Hands iproute2 the lines in one run.
    private bool Batch(string lines, string what)
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, lines);
            var (exitCode, output) = Shell.RunAsync("ip", CancellationToken.None, "-batch", file).GetAwaiter().GetResult();
            if (exitCode != 0)
            {
                _log.Warn("route", $"{what} failed: {output}");
                return false;
            }

            return true;
        }
        catch (IOException ex)
        {
            _log.Error("route", "writing the batch of routes failed", ex);
            return false;
        }
        finally
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException ex)
            {
                _log.Debug("route", $"the batch file {file} stayed behind: {ex.Message}");
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
