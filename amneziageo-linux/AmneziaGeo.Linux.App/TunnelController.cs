using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using AmneziaGeo.Ipc;
using AmneziaGeo.Linux.Engine;
using AmneziaGeo.Routing;

namespace AmneziaGeo.Linux.App;

/// <summary>
/// The peer's last handshake in unix seconds and the bytes it has carried.
/// </summary>
internal readonly record struct PeerCounters(long HandshakeUnix, long RxBytes, long TxBytes);

/// <summary>
/// A refused connect: what it was refused for, and the text that says it.
/// </summary>
internal readonly record struct TunnelFailure(ConnectFailureReason Reason, string Detail);

/// <summary>
/// Brings the amneziawg-go interface up and down over UAPI and iproute2, and applies the routing rules the
/// connection runs under.
/// </summary>
internal sealed class TunnelController : IDisposable
{
    private const string TunDevice = "/dev/net/tun";
    private const string Loopback = "127.0.0.1";
    private const int HandshakeWaitSeconds = 30;
    private const int KeepaliveSeconds = 25;

    private readonly string _enginePath;
    private readonly string _iface;
    private readonly AgentLog _log;
    private AwgDaemon? _daemon;
    private WsCarrier? _carrier;
    private DnsRouter? _dns;
    private RoutingCache? _cache;
    private CancellationTokenSource? _sessionCts;
    private string? _pinnedEndpoint;
    private string _sessionConfig = string.Empty;
    private bool _split;
    private bool _resolverApplied;
    private bool _disposed;

    /// <summary>
    /// ctor
    /// </summary>
    public TunnelController(string enginePath, string interfaceName, AgentLog log)
    {
        _enginePath = enginePath;
        _iface = interfaceName;
        _log = log;
        ResolvConf.Restore(log);
    }

    /// <summary>
    /// Whether the tunnel interface is up.
    /// </summary>
    public bool Running => _daemon is { Running: true };

    /// <summary>
    /// How the running tunnel routes traffic.
    /// </summary>
    public string Mode { get; private set; } = "(down)";

    /// <summary>
    /// How the running tunnel routes, as the journal reads it.
    /// </summary>
    public string RoutingMode { get; private set; } = string.Empty;

    /// <summary>
    /// Routing list in force, empty when none is assigned.
    /// </summary>
    public string ListName { get; private set; } = string.Empty;

    /// <summary>
    /// The name that last resolved the address, empty when none did.
    /// </summary>
    public string NameOf(string address)
    {
        return _dns?.NameOf(address) ?? string.Empty;
    }

    /// <summary>
    /// Address ranges advertised to the engine when the tunnel came up.
    /// </summary>
    public IReadOnlyList<string> Advertised { get; private set; } = [];

    /// <summary>
    /// Destinations routed into the tunnel right now; in a full tunnel nothing has to earn a route into it.
    /// </summary>
    public IReadOnlyCollection<string> Tunneled => _split ? Routed() : [];

    /// <summary>
    /// Destinations pinned to the physical hop right now; in a split they follow it without a route.
    /// </summary>
    public IReadOnlyCollection<string> Bypassed => _split ? [] : Routed();

    /// <summary>
    /// Brings the tunnel up from a wg-quick config under the given rules; returns null on success or the reason
    /// it was refused.
    /// </summary>
    public async Task<TunnelFailure?> UpAsync(string configText, TunnelRouting routing, TunnelOptions options, CancellationToken ct)
    {
        var blocker = Preflight(options.Transport);
        if (blocker is { } refused)
        {
            _log.Warn("tunnel", $"connect refused: {refused.Detail}");
            return refused;
        }

        var (resolved, endpointIp) = await ResolveEndpointAsync(configText, _log, ct).ConfigureAwait(false);
        var carrier = await CarrierAsync(options.Transport, configText, ct).ConfigureAwait(false);
        if (carrier.Refusal is { } refusal)
        {
            _log.Warn("tunnel", $"connect refused: {refusal.Detail}");
            return refusal;
        }

        // The size is read off the link to the server, which the carrier is about to hide behind the loopback.
        var underlay = resolved;
        if (carrier.Started is { } started)
        {
            // The engine dials the carrier instead of the server, and the address that has to stay outside the
            // tunnel is the front's, not the endpoint's.
            endpointIp = carrier.Address;
            resolved = WgConfigEditor.SetEndpoint(resolved, $"{Loopback}:{started.LocalPort}");
        }
        else if (endpointIp is null && WgConfigEditor.GetEndpoint(configText) is { Length: > 0 } named)
        {
            // The engine takes an address and nothing else, so a name left unresolved would come back as errno -22.
            return Refused($"{named} does not resolve, so there is no address to dial");
        }

        var split = routing.Split && routing.HasRules;
        var tunnelResolvers = TunnelResolvers(resolved);
        var startupRoutes = split ? tunnelResolvers.Select(server => $"{server}/32").ToList() : [];
        // Inbound access: what the tunnel may reach this machine from. Off by default.
        var inboundRoutes = options.Transport?.AllowInbound == true
            ? TunnelInbound.Ranges(WgConfigEditor.GetAddresses(resolved), options.Transport.InboundNetwork)
            : [];
        foreach (var inbound in inboundRoutes)
        {
            if (!startupRoutes.Contains(inbound))
            {
                startupRoutes.Add(inbound);
            }
        }

        var allowedIps = AllowedIpsResolver.Build(split, WgConfigEditor.GetAllowedIps(resolved), startupRoutes);
        // Split advertises almost nothing at first, so without a keepalive the peer would only be greeted once
        // some destination had already earned its route - and the name rules that earn it need the tunnel first.
        var config = WgConfigEditor.EnsurePersistentKeepalive(WgConfigEditor.ApplyAllowedIps(resolved, allowedIps), KeepaliveSeconds);

        var uapi = WgQuickToUapi.Convert(config);
        if (uapi is null)
        {
            carrier.Started?.Dispose();
            return Refused("the configuration carries no usable [Interface] PrivateKey");
        }

        await DownAsync(ct).ConfigureAwait(false);
        _carrier = carrier.Started;

        // Read before the tunnel routes land, while the machine's own path is the only one there is.
        var hop = await PhysicalHopAsync(ct).ConfigureAwait(false);
        var lanResolvers = options.LocalResolvers.Count > 0 ? options.LocalResolvers : LanResolvers(hop.Via);

        var daemon = new AwgDaemon(_enginePath, _iface);
        try
        {
            daemon.Start();
            if (daemon.ReclaimedOrphan)
            {
                _log.Warn("tunnel", $"{_iface} was still held by the engine of a run that was killed; it is taken back");
            }

            if (!await WaitForSocketAsync(daemon, ct).ConfigureAwait(false))
            {
                daemon.Dispose();
                return Refused($"amneziawg-go did not open {daemon.SocketPath}; its output is on the agent console");
            }

            await daemon.ConfigureAsync(uapi, ct).ConfigureAwait(false);
            _daemon = daemon;
            _log.Info("tunnel", $"{_iface} configured, endpoint {endpointIp ?? "(none)"}");
        }
        catch (Exception ex)
        {
            _log.Error("tunnel", "engine start failed", ex);
            daemon.Dispose();
            return Refused($"engine start failed: {ex.Message}");
        }

        var failure = await ApplyNetworkAsync(config, allowedIps, endpointIp, MtuPlan.ResolveForLink(options.Transport, underlay), ct).ConfigureAwait(false);
        if (failure is not null)
        {
            await DownAsync(ct).ConfigureAwait(false);
            return Refused(failure);
        }

        Advertised = allowedIps;
        _sessionConfig = configText;
        Mode = split ? $"split ({routing.ListName})" : routing.HasRules ? $"full ({routing.ListName})" : "full";
        RoutingMode = Token(split, routing.HasRules);
        ListName = routing.ListName;
        _split = split;
        var applier = new LinuxRouteApplier(_iface, PeerKeyHex(config), daemon, hop.Via, hop.Dev, allowedIps, endpointIp, _log);
        // The resolver addresses are handed over as pinned: a list range that covers one would otherwise make the
        // cache own its route and reclaim it as idle, taking the tunnel's own name lookups down with it.
        var cache = new RoutingCache(applier, new ProcNet(), split, routing.ProxyRoutes, routing.DirectRoutes, routing.BlockRoutes, options.RouteTtlSeconds, new AgentLogger<RoutingCache>(_log, "route"), [.. tunnelResolvers.Select(server => server.ToString()), .. inboundRoutes]);
        _cache = cache;
        _sessionCts = new CancellationTokenSource();
        _ = Task.Run(() => cache.RunAsync(_sessionCts.Token));
        StartNameRouter(routing with { Split = split }, allowedIps, tunnelResolvers, lanResolvers);
        var ranges = cache.RangeCounts;
        _log.Info("tunnel", $"routing {Mode}: {allowedIps.Count} range(s) advertised, {ranges.Proxy} range(s) go through the tunnel, {ranges.Direct} stay outside it, {ranges.Block} are refused; each address is decided on first contact and forgotten after {options.RouteTtlSeconds} s unused");
        return null;
    }

    /// <summary>
    /// Sets how long a destination keeps its route on the running connection.
    /// </summary>
    public void SetRouteTtl(int seconds) => _cache?.SetTtl(seconds);

    /// <summary>
    /// Binds the tunnel socket to another source port, leaving the session, its routes and its DNS standing. A
    /// NAT that has dropped the mapping keeps discarding what the old port sends.
    /// </summary>
    public async Task<bool> RebindAsync(CancellationToken ct)
    {
        if (_daemon is not { Running: true } daemon)
        {
            return false;
        }

        try
        {
            await daemon.ConfigureAsync("listen_port=0", ct).ConfigureAwait(false);
            _log.Info("tunnel", $"{_iface} is bound to another source port");
            return true;
        }
        catch (Exception ex)
        {
            _log.Warn("tunnel", $"binding {_iface} to another source port failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Resolves the server's address again and hands it to the peer, for a server that has moved. A carried
    /// tunnel dials its carrier on loopback and is left to the carrier.
    /// </summary>
    public async Task<bool> RepointAsync(CancellationToken ct)
    {
        if (_daemon is not { Running: true } daemon || _carrier is not null || _sessionConfig.Length == 0)
        {
            return false;
        }

        var (resolved, endpointIp) = await ResolveEndpointAsync(_sessionConfig, _log, ct).ConfigureAwait(false);
        var endpoint = WgConfigEditor.GetEndpoint(resolved);
        var peer = PeerKeyHex(resolved);
        if (endpointIp is null || string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(peer))
        {
            return false;
        }

        try
        {
            await daemon.ConfigureAsync($"public_key={peer}\nendpoint={endpoint}", ct).ConfigureAwait(false);
            _log.Info("tunnel", $"{_iface} now dials {endpoint}");
            return true;
        }
        catch (Exception ex)
        {
            _log.Warn("tunnel", $"pointing {_iface} at {endpoint} failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// The routing cache of the running tunnel, or none when nothing runs.
    /// </summary>
    public RoutingCache? Cache => _cache;

    /// <summary>
    /// Applies edited rules to the running tunnel and drops every verdict taken under the old ones; false when
    /// only a fresh tunnel can carry the change, because the mode decides what the engine was told to accept.
    /// </summary>
    public bool ApplyRules(TunnelRouting routing)
    {
        if (_cache is not { } cache)
        {
            return false;
        }

        if ((routing.Split && routing.HasRules) != _split)
        {
            return false;
        }

        cache.Rebuild(routing.ProxyRoutes, routing.DirectRoutes, routing.BlockRoutes);
        _dns?.ApplyRules(routing);
        Mode = _split ? $"split ({routing.ListName})" : routing.HasRules ? $"full ({routing.ListName})" : "full";
        RoutingMode = Token(_split, routing.HasRules);
        ListName = routing.ListName;
        return true;
    }

    /// <summary>
    /// Tears the tunnel down; the interface goes with the daemon process.
    /// </summary>
    public async Task DownAsync(CancellationToken ct = default)
    {
        if (_sessionCts is { } cts)
        {
            _sessionCts = null;
            await cts.CancelAsync().ConfigureAwait(false);
            cts.Dispose();
        }

        _dns?.Dispose();
        _dns = null;
        if (_resolverApplied)
        {
            _resolverApplied = false;
            ResolvConf.Restore(_log);
        }

        if (_cache is { } cache)
        {
            _cache = null;
            cache.RemoveAll();
        }

        if (_pinnedEndpoint is { } pinned)
        {
            _pinnedEndpoint = null;
            await Shell.RunAsync("ip", ct, "route", "del", pinned).ConfigureAwait(false);
        }

        if (_carrier is { } carrier)
        {
            _carrier = null;
            carrier.Dispose();
        }

        if (_daemon is { } daemon)
        {
            _daemon = null;
            daemon.Dispose();
            _log.Info("tunnel", $"{_iface} down");
        }

        Mode = "(down)";
        RoutingMode = string.Empty;
        ListName = string.Empty;
        Advertised = [];
        _split = false;
    }

    // The addresses holding a host route: into the tunnel in a split, out the physical hop in a full tunnel.
    // How the session routes, for the journal that reports on it.
    private static string Token(bool split, bool hasRules)
    {
        if (split)
        {
            return SessionReport.ModeSplit;
        }

        return hasRules ? SessionReport.ModeFull : SessionReport.ModeOff;
    }

    private IReadOnlyCollection<string> Routed()
    {
        var routed = new List<string>();
        foreach (var entry in _cache?.Snapshot() ?? [])
        {
            if (entry.Routed)
            {
                routed.Add(entry.Address.ToString());
            }
        }

        return routed;
    }

    // Binds the name router and points the machine at it once the peer answers, so a dial that never completes
    // cannot leave the machine without a resolver.
    private void StartNameRouter(TunnelRouting routing, IReadOnlyList<string> allowedIps, IReadOnlyList<IPAddress> tunnelResolvers, IReadOnlyList<IPAddress> lanResolvers)
    {
        var stripV6 = !allowedIps.Any(range => range.Contains(':', StringComparison.Ordinal));
        var router = new DnsRouter(routing, stripV6, tunnelResolvers, lanResolvers, _cache!, _log);
        if (!router.Start())
        {
            router.Dispose();
            _log.Warn("dns", "the name router could not bind, so rules by domain name do not apply; only rules by address do");
            return;
        }

        _dns = router;
        _ = Task.Run(() => ApplyResolverWhenUpAsync(_sessionCts!.Token));
    }

    private async Task ApplyResolverWhenUpAsync(CancellationToken ct)
    {
        try
        {
            for (var attempt = 0; attempt < HandshakeWaitSeconds * 2; attempt++)
            {
                if (await HandshakeSeenAsync(ct).ConfigureAwait(false))
                {
                    _resolverApplied = ResolvConf.Apply(DnsRouter.Listen, _log);
                    _log.Info("dns", $"lookups now go to {DnsRouter.Listen}");
                    return;
                }

                await Task.Delay(500, ct).ConfigureAwait(false);
            }

            _log.Warn("dns", "the server never answered, so the machine keeps its own resolver and rules by domain name do not apply");
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// The peer's last handshake and its byte counters, or null when nothing runs.
    /// </summary>
    public async Task<PeerCounters?> PeerCountersAsync(CancellationToken ct)
    {
        if (_daemon is not { } daemon)
        {
            return null;
        }

        try
        {
            var handshake = 0L;
            var rx = 0L;
            var tx = 0L;
            foreach (var line in (await daemon.GetConfigAsync(ct).ConfigureAwait(false)).Split('\n'))
            {
                if (line.StartsWith("last_handshake_time_sec=", StringComparison.Ordinal)
                    && long.TryParse(line.AsSpan(24), out var seconds) && seconds > handshake)
                {
                    handshake = seconds;
                }
                else if (line.StartsWith("rx_bytes=", StringComparison.Ordinal)
                    && long.TryParse(line.AsSpan(9), out var received))
                {
                    rx += received;
                }
                else if (line.StartsWith("tx_bytes=", StringComparison.Ordinal)
                    && long.TryParse(line.AsSpan(9), out var sent))
                {
                    tx += sent;
                }
            }

            return new PeerCounters(handshake, rx, tx);
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
        }

        return null;
    }

    // Whether the peer has answered at least once.
    private async Task<bool> HandshakeSeenAsync(CancellationToken ct)
    {
        if (_daemon is not { } daemon)
        {
            return false;
        }

        try
        {
            foreach (var line in (await daemon.GetConfigAsync(ct).ConfigureAwait(false)).Split('\n'))
            {
                if (line.StartsWith("last_handshake_time_sec=", StringComparison.Ordinal)
                    && long.TryParse(line.AsSpan(24), out var seconds) && seconds > 0)
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
        }

        return false;
    }

    // Refuses the connect with an actionable reason when the host cannot carry a tunnel.
    private TunnelFailure? Preflight(ConfigTransport? transport)
    {
        if (!File.Exists(_enginePath))
        {
            return Refused($"the amneziawg-go binary is missing at {_enginePath}; build it with amneziageo-linux/tools/build-engine-linux.sh and rebuild the agent");
        }

        if (geteuid() != 0)
        {
            return Refused("creating the tunnel interface needs root; start the agent from \"Debug Linux\" with \"sudo\": true in .vscode/debug.linux.jsonc, or with: sudo dotnet AmneziaGeo.Linux.App.dll");
        }

        if (!File.Exists(TunDevice))
        {
            return Refused($"{TunDevice} is missing; load the module with: sudo modprobe tun");
        }

        // The engine hands the carrier every packet on the loopback, so a firewall that drops UDP there leaves
        // the tunnel silent with nothing to show for it.
        if (transport?.UseWebSocket == true && !WsCarrier.LoopbackCarries())
        {
            return new TunnelFailure(
                ConnectFailureReason.LoopbackBlocked,
                $"UDP does not cross the loopback on this machine, and the engine dials the websocket carrier on {Loopback}; let UDP through on lo");
        }

        return null;
    }

    // The resolvers reached through the tunnel: the config's own IPv4 ones, or Cloudflare when it names none.
    private static IReadOnlyList<IPAddress> TunnelResolvers(string config)
    {
        var servers = new List<IPAddress>();
        foreach (var entry in WgConfigEditor.GetDns(config))
        {
            if (IPAddress.TryParse(entry, out var address) && address.AddressFamily == AddressFamily.InterNetwork)
            {
                servers.Add(address);
            }
        }

        return servers.Count > 0 ? servers : [IPAddress.Parse("1.1.1.1")];
    }

    // The resolvers of the machine's own network, falling back to the gateway when the file names none.
    private static IReadOnlyList<IPAddress> LanResolvers(string? gateway)
    {
        var servers = ResolvConf.CaptureUpstream();
        if (servers.Count > 0)
        {
            return servers;
        }

        return gateway is not null && IPAddress.TryParse(gateway, out var address) ? [address] : [];
    }

    // Carries a refusal the agent has no cause of its own for.
    private static TunnelFailure Refused(string detail) => new(ConnectFailureReason.ServiceStartFailed, detail);

    // The websocket carrier a configuration asks for: the engine dials it on the loopback and it wraps the
    // tunnel in web traffic the network lets through. The front is resolved here, before the tunnel takes over
    // the machine's routes, because a lookup made afterwards would travel inside the tunnel it is meant to open.
    private async Task<(WsCarrier? Started, string? Address, TunnelFailure? Refusal)> CarrierAsync(ConfigTransport? transport, string configText, CancellationToken ct)
    {
        if (transport?.UseWebSocket != true)
        {
            return (null, null, null);
        }

        var endpoint = WgConfigEditor.GetEndpoint(configText) ?? string.Empty;
        var colon = endpoint.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(endpoint[(colon + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var targetPort))
        {
            return (null, null, Refused("this configuration asks to be carried inside a websocket, but its Endpoint names no port"));
        }

        var front = WsEndpoint.Parse(transport.WebSocketHost, transport.WebSocketPort, endpoint[..colon].Trim('[', ']'));
        var address = await ResolveHostAsync(front.Host, ct).ConfigureAwait(false);
        if (address is null || front.Port <= 0)
        {
            return (null, null, Refused($"the websocket front {front.Host}:{front.Port} has no address to dial"));
        }

        var carrier = WsCarrier.Start(front, address, targetPort, null, Note);
        _log.Info("tunnel", $"the tunnel is carried inside a websocket to {front.Host}:{front.Port}; the engine dials it on {Loopback}:{carrier.LocalPort}");
        return (carrier, address.ToString(), null);
    }

    // What the carrier has to say, at the level its news deserves.
    private void Note(string message, Exception? ex)
    {
        if (ex is null)
        {
            _log.Info("tunnel", message);
        }
        else
        {
            _log.Error("tunnel", message, ex);
        }
    }

    // One address for a host, as the tunnel dials it.
    private static async Task<IPAddress?> ResolveHostAsync(string host, CancellationToken ct)
    {
        if (host.Length == 0)
        {
            return null;
        }

        if (IPAddress.TryParse(host, out var literal))
        {
            return literal;
        }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
            return Array.Find(addresses, one => one.AddressFamily == AddressFamily.InterNetwork);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException or OperationCanceledException)
        {
            return null;
        }
    }

    // The engine does not resolve names, so a hostname endpoint is rewritten to its address.
    private static async Task<(string Config, string? EndpointIp)> ResolveEndpointAsync(string config, AgentLog log, CancellationToken ct)
    {
        var endpoint = WgConfigEditor.GetEndpoint(config);
        var colon = endpoint?.LastIndexOf(':') ?? -1;
        if (endpoint is null || colon <= 0)
        {
            return (config, null);
        }

        var host = endpoint[..colon].Trim('[', ']');
        var port = endpoint[(colon + 1)..];
        if (IPAddress.TryParse(host, out var literal))
        {
            return (config, literal.ToString());
        }

        var addresses = Array.Empty<IPAddress>();
        try
        {
            addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            log.Warn("tunnel", $"{host} does not resolve: {ex.Message}");
            return (config, null);
        }

        var resolved = Array.Find(addresses, a => a.AddressFamily == AddressFamily.InterNetwork) ?? Array.Find(addresses, _ => true);
        return resolved is null
            ? (config, null)
            : (WgConfigEditor.SetEndpoint(config, $"{resolved}:{port}"), resolved.ToString());
    }

    // Waits for the daemon to publish its control socket.
    private static async Task<bool> WaitForSocketAsync(AwgDaemon daemon, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (File.Exists(daemon.SocketPath))
            {
                return true;
            }

            if (!daemon.Running)
            {
                return false;
            }

            await Task.Delay(100, ct).ConfigureAwait(false);
        }

        return false;
    }

    // Addresses, MTU, and the routes for the ranges the tunnel starts with.
    private async Task<string?> ApplyNetworkAsync(string config, IReadOnlyList<string> allowedIps, string? endpointIp, int mtu, CancellationToken ct)
    {
        foreach (var address in WgConfigEditor.GetAddresses(config))
        {
            var family = address.Contains(':', StringComparison.Ordinal) ? "-6" : "-4";
            var added = await Shell.RunAsync("ip", ct, family, "address", "add", address, "dev", _iface).ConfigureAwait(false);
            if (added.ExitCode != 0)
            {
                return $"ip address add {address} failed: {added.Output}";
            }
        }

        var up = await Shell.RunAsync("ip", ct, "link", "set", "dev", _iface, "mtu", mtu.ToString(CultureInfo.InvariantCulture), "up").ConfigureAwait(false);
        if (up.ExitCode != 0)
        {
            return $"ip link set up failed: {up.Output}";
        }

        if (endpointIp is not null)
        {
            await PinEndpointAsync(endpointIp, ct).ConfigureAwait(false);
        }

        foreach (var allowed in allowedIps)
        {
            foreach (var route in ExpandRoute(allowed))
            {
                var added = await Shell.RunAsync("ip", ct, "route", "replace", route, "dev", _iface).ConfigureAwait(false);
                if (added.ExitCode == 0)
                {
                    _log.Route($"{route} dev {_iface}");
                }
                else
                {
                    _log.Warn("tunnel", $"ip route add {route} failed: {added.Output}");
                }
            }
        }

        return null;
    }

    // A default route is laid as two halves so it outranks the physical one without replacing it.
    private static IEnumerable<string> ExpandRoute(string cidr)
    {
        return cidr switch
        {
            "0.0.0.0/0" => ["0.0.0.0/1", "128.0.0.0/1"],
            "::/0" => ["::/1", "8000::/1"],
            _ => [cidr],
        };
    }

    // The hop the machine reaches the internet through, which a bypassed destination is pinned to.
    private async Task<(string? Via, string? Dev)> PhysicalHopAsync(CancellationToken ct)
    {
        var lookup = await Shell.RunAsync("ip", ct, "route", "show", "default").ConfigureAwait(false);
        if (lookup.ExitCode != 0)
        {
            return (null, null);
        }

        foreach (var line in lookup.Output.Split('\n'))
        {
            var via = Shell.Token(line, "via");
            var dev = Shell.Token(line, "dev");
            if (via is not null && dev is not null && dev != _iface)
            {
                return (via, dev);
            }
        }

        return (null, null);
    }

    // Keeps the peer reachable off-tunnel while a default route is in place.
    private async Task PinEndpointAsync(string endpointIp, CancellationToken ct)
    {
        var lookup = await Shell.RunAsync("ip", ct, "route", "get", endpointIp).ConfigureAwait(false);
        if (lookup.ExitCode != 0)
        {
            return;
        }

        var via = Shell.Token(lookup.Output, "via");
        var dev = Shell.Token(lookup.Output, "dev");
        if (via is null || dev is null || dev == _iface)
        {
            return;
        }

        var pinned = await Shell.RunAsync("ip", ct, "route", "add", endpointIp, "via", via, "dev", dev).ConfigureAwait(false);
        if (pinned.ExitCode == 0)
        {
            _pinnedEndpoint = endpointIp;
            _log.Route($"{endpointIp} via {via} dev {dev}");
        }
    }

    // The peer key in the form the engine's control channel takes.
    private static string? PeerKeyHex(string config)
    {
        var key = WgConfigEditor.GetPeerPublicKey(config);
        if (key is null)
        {
            return null;
        }

        try
        {
            var bytes = Convert.FromBase64String(key);
            return bytes.Length == 32 ? Convert.ToHexStringLower(bytes) : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern uint geteuid();

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DownAsync().GetAwaiter().GetResult();
    }
}
