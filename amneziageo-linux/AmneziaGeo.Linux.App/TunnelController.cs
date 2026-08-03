using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using AmneziaGeo.Geo;
using AmneziaGeo.Linux.Engine;

namespace AmneziaGeo.Linux.App;

/// <summary>
/// Brings the amneziawg-go interface up and down over UAPI and iproute2, and applies the routing rules the
/// connection runs under.
/// </summary>
internal sealed class TunnelController : IDisposable
{
    private const string TunDevice = "/dev/net/tun";
    private const int DefaultMtu = 1420;
    private const int HandshakeWaitSeconds = 30;
    private const int KeepaliveSeconds = 25;

    private readonly string _enginePath;
    private readonly string _iface;
    private readonly AgentLog _log;
    private AwgDaemon? _daemon;
    private DnsRouter? _dns;
    private LiveRoutes? _routes;
    private CancellationTokenSource? _sessionCts;
    private string? _pinnedEndpoint;
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
    /// Address ranges advertised to the engine when the tunnel came up.
    /// </summary>
    public IReadOnlyList<string> Advertised { get; private set; } = [];

    /// <summary>
    /// Destinations routed into the tunnel since it came up.
    /// </summary>
    public IReadOnlyCollection<string> Tunneled => _routes?.Tunneled ?? [];

    /// <summary>
    /// Destinations pinned to the physical hop since the tunnel came up.
    /// </summary>
    public IReadOnlyCollection<string> Bypassed => _routes?.Bypassed ?? [];

    /// <summary>
    /// Brings the tunnel up from a wg-quick config under the given rules; returns null on success or the reason
    /// it was refused.
    /// </summary>
    public async Task<string?> UpAsync(string configText, TunnelRouting routing, CancellationToken ct)
    {
        var blocker = Preflight();
        if (blocker is not null)
        {
            _log.Warn("tunnel", $"connect refused: {blocker}");
            return blocker;
        }

        var (resolved, endpointIp) = await ResolveEndpointAsync(configText, ct).ConfigureAwait(false);
        var split = routing.Split && routing.HasRules;
        var tunnelResolvers = TunnelResolvers(resolved);
        var startupRoutes = split ? tunnelResolvers.Select(server => $"{server}/32").ToList() : [];
        var allowedIps = AllowedIpsResolver.Build(split, WgConfigEditor.GetAllowedIps(resolved), startupRoutes);
        // Split advertises almost nothing at first, so without a keepalive the peer would only be greeted once
        // some destination had already earned its route - and the name rules that earn it need the tunnel first.
        var config = WgConfigEditor.EnsurePersistentKeepalive(WgConfigEditor.ApplyAllowedIps(resolved, allowedIps), KeepaliveSeconds);

        var uapi = WgQuickToUapi.Convert(config);
        if (uapi is null)
        {
            return "the configuration carries no usable [Interface] PrivateKey";
        }

        await DownAsync(ct).ConfigureAwait(false);

        // Read before the tunnel routes land, while the machine's own path is the only one there is.
        var hop = await PhysicalHopAsync(ct).ConfigureAwait(false);
        var lanResolvers = LanResolvers(hop.Via);

        var daemon = new AwgDaemon(_enginePath, _iface);
        try
        {
            daemon.Start();
            if (!await WaitForSocketAsync(daemon, ct).ConfigureAwait(false))
            {
                daemon.Dispose();
                return $"amneziawg-go did not open {daemon.SocketPath}; its output is on the agent console";
            }

            await daemon.ConfigureAsync(uapi, ct).ConfigureAwait(false);
            _daemon = daemon;
            _log.Info("tunnel", $"{_iface} configured, endpoint {endpointIp ?? "(none)"}");
        }
        catch (Exception ex)
        {
            _log.Error("tunnel", "engine start failed", ex);
            daemon.Dispose();
            return $"engine start failed: {ex.Message}";
        }

        var failure = await ApplyNetworkAsync(config, allowedIps, endpointIp, ct).ConfigureAwait(false);
        if (failure is not null)
        {
            await DownAsync(ct).ConfigureAwait(false);
            return failure;
        }

        Advertised = allowedIps;
        Mode = split ? $"split ({routing.ListName})" : routing.HasRules ? $"full ({routing.ListName})" : "full";
        _routes = new LiveRoutes(_iface, PeerKeyHex(config), daemon, hop.Via, hop.Dev, _log);
        StartNameRouter(routing with { Split = split }, tunnelResolvers, lanResolvers);
        _log.Info("tunnel", $"routing {Mode}: {allowedIps.Count} range(s) advertised, {routing.ProxyRoutes.Count} proxy range(s) and {routing.ProxyDomains.Count} domain(s) decided per destination");
        return null;
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

        if (_routes is { } routes)
        {
            _routes = null;
            await routes.ClearAsync(ct).ConfigureAwait(false);
        }

        if (_pinnedEndpoint is { } pinned)
        {
            _pinnedEndpoint = null;
            await Shell.RunAsync("ip", ct, "route", "del", pinned).ConfigureAwait(false);
        }

        if (_daemon is { } daemon)
        {
            _daemon = null;
            daemon.Dispose();
            _log.Info("tunnel", $"{_iface} down");
        }

        Mode = "(down)";
        Advertised = [];
    }

    // Binds the name router and points the machine at it once the peer answers, so a dial that never completes
    // cannot leave the machine without a resolver.
    private void StartNameRouter(TunnelRouting routing, IReadOnlyList<IPAddress> tunnelResolvers, IReadOnlyList<IPAddress> lanResolvers)
    {
        var router = new DnsRouter(routing, tunnelResolvers, lanResolvers, _routes!, _log);
        if (!router.Start())
        {
            router.Dispose();
            _log.Warn("dns", "the name router could not bind, so rules by domain name do not apply; only rules by address do");
            return;
        }

        _dns = router;
        _sessionCts = new CancellationTokenSource();
        _ = Task.Run(() => ApplyResolverWhenUpAsync(_sessionCts.Token));
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
    private string? Preflight()
    {
        if (!File.Exists(_enginePath))
        {
            return $"the amneziawg-go binary is missing at {_enginePath}; build it with amneziageo-linux/tools/build-engine-linux.sh and rebuild the agent";
        }

        if (geteuid() != 0)
        {
            return "creating the tunnel interface needs root; start the agent from \"Debug Linux (agent)\", the \"Run Linux agent (sudo)\" task, or with: sudo dotnet AmneziaGeo.Linux.App.dll";
        }

        if (!File.Exists(TunDevice))
        {
            return $"{TunDevice} is missing; load the module with: sudo modprobe tun";
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

    // The engine does not resolve names, so a hostname endpoint is rewritten to its address.
    private static async Task<(string Config, string? EndpointIp)> ResolveEndpointAsync(string config, CancellationToken ct)
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

        var addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
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
    private async Task<string?> ApplyNetworkAsync(string config, IReadOnlyList<string> allowedIps, string? endpointIp, CancellationToken ct)
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

        var mtu = WgConfigEditor.GetMtu(config);
        var up = await Shell.RunAsync("ip", ct, "link", "set", "dev", _iface, "mtu", (mtu > 0 ? mtu : DefaultMtu).ToString(CultureInfo.InvariantCulture), "up").ConfigureAwait(false);
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
