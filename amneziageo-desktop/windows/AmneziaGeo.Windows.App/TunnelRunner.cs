using System.Net;
using System.Net.Sockets;
using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using AmneziaGeo.Windows.Engine;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Runs a tunnel inside the Windows service process.
/// </summary>
internal sealed class TunnelRunner(
    IStateStore store,
    SettingsStore settings,
    RouteManager routes,
    UapiClient uapi,
    DnsConfigurator dns,
    NetworkReconciler reconciler,
    WindowsFirewall firewall,
    ILoggerFactory loggerFactory,
    ILogger<TunnelRunner> logger)
{
    // Tunnel MTU forced while the WSS (wstunnel) transport is active. 1280 is the IPv6 minimum and
    // leaves headroom for the WG (~80B) + TLS/WebSocket/TCP (~90B) overhead the WSS underlay adds, so an
    // inner full-size segment still fits one physical 1500-byte segment and a single underlay loss does
    // not stall a two-segment inner packet (#77).
    private const int WsTunnelMtu = 1280;

    /// <summary>
    /// Loads the tunnel config and materialized geo set, then hands control to the native service loop. On
    /// a setup failure it forwards the reason to the shared store so the agent can surface it in the UI
    /// journal - this per-tunnel service process's own log file (ageo-DATE_NNN.log) is not shown in the UI.
    /// </summary>
    public async Task RunAsync(string name)
    {
        try
        {
            await RunInnerAsync(name);
        }
        catch (Exception ex)
        {
            // Best-effort: persist the failure reason for the agent to re-log; never mask the original.
            try
            {
                await store.SetSettingAsync(TunnelPaths.ConnectMessageKey(name), ex.Message);
            }
            catch
            {
            }

            throw;
        }
    }

    private async Task RunInnerAsync(string name)
    {
        // Start from a clean slate: revert any DNS/route leftovers from a previous tunnel.
        reconciler.Reconcile();

        var config = await store.GetConfigTextAsync(name)
            ?? throw new InvalidOperationException($"configuration '{name}' is not stored");

        // Resolve the WebSocket (UDP-over-TCP) transport plan up front from the ORIGINAL endpoint, but
        // start the wstunnel child last (just before the engine) so a setup failure cannot orphan it.
        // When enabled, the engine dials a loopback wstunnel port and the real server is reached
        // out-of-band over TCP/TLS, so the endpoint exclusion below must target the real server IP, not
        // loopback.
        var transport = await store.GetConfigTransportAsync(name);
        var useWebSocket = transport?.UseWebSocket == true;
        string? wsHost = null;            // the wstunnel server host the WSS connection dials
        var wsPort = 0;                   // the wstunnel server TLS port
        var wsTargetPort = 0;             // server-side AmneziaWG UDP port = the original Endpoint's port
        var wsPathPrefix = string.Empty;  // optional auth/anti-probe path token
        var wsCredentials = string.Empty; // optional basic-auth "user[:pass]"
        IPAddress? wsServerIp = null;     // resolved wsHost, excluded so wstunnel's own TCP/TLS stays off-tunnel
        if (useWebSocket)
        {
            var parsed = ParseEndpoint(WgConfigEditor.GetEndpoint(config));
            if (parsed is null)
            {
                logger.LogError("websocket transport: config {Name} has no usable Endpoint; using plain UDP", name);
                useWebSocket = false;
            }
            else
            {
                var (endpointHost, endpointPort) = parsed.Value;
                wsTargetPort = endpointPort;
                // The host field defaults to the config's own Endpoint host but may carry a full
                // wss://[user:pass@]host[:port]/[token] URL (separate WS front, plus optional auth in one
                // string). Resolve the resulting host for the exclusion route, since that is where wstunnel
                // opens its TCP/TLS connection.
                var ws = WsEndpoint.Parse(transport!.WebSocketHost, transport.WebSocketPort, endpointHost);
                wsHost = ws.Host;
                wsPort = ws.Port;
                wsPathPrefix = ws.PathPrefix;
                wsCredentials = ws.Credentials;
                wsServerIp = ResolveHostV4(wsHost);
            }
        }

        WsTunnelTransport? wsTransport = null;

        // Prefer the live balancer routing projection; fall back to the config's own set-geo split.
        var geo = await store.GetActiveTunnelGeoAsync(name);

        var geoSplit = geo?.GeoSplit ?? false;
        var geoRoutes = new List<string>(geo?.Routes ?? []);
        var domains = geo?.Domains ?? [];
        var apps = geo?.Apps ?? [];

        // This tunnel adapter is IPv4-only when the config declares no IPv6 Address.
        var stripV6 = !HasIpv6Address(config);

        // The proxy tracks matched domains only in split mode (in full tunnel everything is already
        // routed; tracking would replace 0.0.0.0/0 with a few /32s).
        var trackDomains = geoSplit && domains.Count > 0;

        // Per-app tunneling (#68) is also a split-only concept: the app route watcher feeds matched apps'
        // remote IPs into the shared domain tracker. In full tunnel everything is already routed.
        var trackApps = geoSplit && apps.Count > 0;

        // Clean resolver for TUNNELED (matched) names = the config's own DNS, reached THROUGH the tunnel.
        // A geo-blocked domain must resolve here, not via the local network resolver, which hands back a
        // poisoned/blocked answer (e.g. chatgpt.com -> a sinkhole IP) that then gets routed into the
        // tunnel to nowhere. Add the resolver's address to the tunneled routes so it is reachable.
        var configDns = WgConfigEditor.GetDns(config);
        IReadOnlyList<string> tunnelResolver = configDns.Count > 0 ? configDns : ["1.1.1.1"];
        if (trackDomains)
        {
            foreach (var server in tunnelResolver)
            {
                var route = $"{server}/32";
                if (IPAddress.TryParse(server, out _) && !geoRoutes.Contains(route))
                {
                    geoRoutes.Add(route);
                }
            }
        }

        var allowedIps = AllowedIpsResolver.Build(geoSplit, WgConfigEditor.GetAllowedIps(config), geoRoutes);
        if (stripV6)
        {
            // Never hand IPv6 routes (e.g. a full-tunnel config's ::/0) to a v4-only tunnel: the engine
            // would route IPv6 into a tunnel with no transit while clients still try IPv6 first.
            allowedIps = [.. allowedIps.Where(a => !a.Contains(':'))];
        }

        // Split any default route (0.0.0.0/0 or ::/0) into its two halves before handing AllowedIPs to
        // the engine. A peer with a /0 AllowedIP makes the engine arm its own blanket kill-switch
        // firewall (amneziawg-windows tunnel/addressconfig.go: doNotRestrict=false), which blocks the LAN
        // and severs host/Hyper-V SSH and has no LAN bypass. The two /1 halves cover the same address
        // space for routing but are not /0, so the engine routes the full tunnel WITHOUT arming that
        // firewall. Our own WindowsFirewall provides the kill-switch (with a LAN bypass) instead.
        allowedIps = SplitDefaultRoutes(allowedIps);

        config = WgConfigEditor.ApplyAllowedIps(config, allowedIps);

        var appSettings = await settings.LoadAsync();

        // Wrap ALL UDP through the tunnel (#77-udp): split-only catch-all for real-time media whose server
        // IPs arrive via signaling, not DNS. Drives the ETW UDP tracker's no-PID-filter mode below. In full
        // tunnel everything is already routed, so it is a no-op there.
        var allUdp = geoSplit && appSettings.TunnelAllUdp;

        // Capture the resolvers the system uses now, before redirecting, so the proxy can forward
        // NON-tunneled queries to them - keeps corporate/existing name resolution working alongside
        // another VPN. The loopback proxy always runs (split AND full): on this IPv4-only tunnel it
        // denies AAAA and HTTPS/SVCB (type 65) so dual-stack clients don't stall and Chrome doesn't take
        // an HTTP/3 hint path that bypasses the tunnel.
        // This profile's (config's) "preferred DNS" overrides the auto-detected system resolvers for
        // NON-tunneled names; empty falls back to what the system uses now. Tunneled (geo-matched) names keep
        // the clean tunnelResolver above, so this never weakens geo resolution. Per-config (moved off the
        // former global app setting) so each profile carries its own DNS.
        var dnsOverride = await store.GetConfigDnsAsync(name);
        var preferredDns = ParseDnsServers(dnsOverride?.Servers ?? string.Empty);
        // The real system/LAN resolver, captured regardless of any preferred-DNS override: LOCAL names
        // (the LAN, corporate, and user exclusion-list domains) must keep resolving HERE, not offshore -
        // this is what keeps the local network alive in full tunnel and fixes a preferred-DNS set to a
        // public resolver from swallowing local lookups.
        var lanResolvers = dns.CaptureUpstream();
        var upstream = preferredDns.Count > 0 ? preferredDns : lanResolvers;
        // Per-config bypass list (moved off the former global setting), with the same defaulting as the old
        // global: an empty list and auto-exclude-LAN on when the config has no stored exclusions yet.
        var configExclusions = await store.GetConfigExclusionsAsync(name);
        var (parsedCidrs, parsedExclusionDomains) = ParseExclusions(configExclusions?.Exclusions ?? string.Empty);
        var exclusionDomains = new List<string>(parsedExclusionDomains);
        // Treat the network's own DNS suffix(es) - the corp/LAN domain DHCP advertises - as local, so a
        // corporate host published under a public-looking FQDN via split-horizon DNS (mail.company.com -> an
        // internal IP) resolves via the LAN resolver and stays off the tunnel. Without this, full tunnel
        // sends such names to the offshore resolver (a public/empty answer) and corp services break. Captured
        // before the DNS redirect; applies in both modes (harmless in split, where these already resolve via
        // the system resolver).
        foreach (var suffix in dns.CaptureLocalDnsSuffixes())
        {
            if (!exclusionDomains.Contains(suffix))
            {
                exclusionDomains.Add(suffix);
            }
        }

        if (exclusionDomains.Count > parsedExclusionDomains.Count)
        {
            logger.LogInformation("local DNS suffixes kept on-LAN: {Suffixes}", string.Join(", ", exclusionDomains.Skip(parsedExclusionDomains.Count)));
        }
        // The WebSocket underlay server's hostname must resolve via the LAN resolver, never through the
        // tunnel: in full tunnel non-matched names go to the tunnel resolver, but the tunnel is not up yet
        // and cannot come up until wstunnel reaches the server, whose name it must resolve first. Treat it
        // as a split-DNS local domain so it resolves out-of-band (its route is already excluded above).
        if (useWebSocket && wsHost is not null && !IPAddress.TryParse(wsHost, out _))
        {
            exclusionDomains.Add(wsHost);
        }
        // The default bypass set (the RFC1918 ranges + the machine's currently-connected subnets) is computed
        // at runtime, never stored: with no exclusions row it is what keeps the LAN direct in full tunnel, so
        // it always matches the network the host is on right now. Once the user saves a row, that list is
        // authoritative (they can pare it down - even drop the LAN ranges - or extend it).
        var exclusionCidrs = configExclusions is null
            ? new List<string>(routes.DefaultExclusionEntries())
            : new List<string>(parsedCidrs);

        IReadOnlyList<string> redirectServers = [];

        // Local resolver for NON-tunneled names: in split the captured system resolver (coexisting /
        // corporate names keep resolving); in full tunnel everything is tunneled, so resolve all names
        // via the clean resolver above.
        var localResolver = geoSplit
            ? (upstream.Count > 0 ? upstream : tunnelResolver)
            : tunnelResolver;

        // The domain tracker owns the dynamic /32 routes and the single allowed-ips authority. It is shared
        // by the DNS path (matched domains) and the app route watcher (matched apps), so create it once when
        // either is active and hand it to both - two independent SetAllowedIps owners would clobber.
        DomainTracker? tracker = null;
        if (trackDomains || trackApps || allUdp)
        {
            var peer = WgConfigEditor.GetPeerPublicKey(config);
            if (peer is not null)
            {
                tracker = new DomainTracker(store, routes, uapi, loggerFactory.CreateLogger<DomainTracker>(), name, peer, geoRoutes, appSettings.RefreshSeconds, stripV6);
                _ = Task.Run(tracker.RunAsync);
            }
        }

        var proxy = StartProxy(trackDomains ? domains : [], stripV6, tunnelResolver, localResolver, lanResolvers, exclusionDomains, tracker);
        if (proxy?.BoundV4 is not null)
        {
            redirectServers = [proxy.BoundV4.ToString()];
        }
        else
        {
            // Could not bind loopback :53 (another resolver holds it): degrade to setting resolvers
            // directly - no AAAA/type-65 deny, but connectivity rather than a hang.
            redirectServers = configDns.Count > 0 ? configDns : upstream;
            logger.LogWarning("DNS proxy unavailable (loopback :53 busy); using direct resolvers");
        }

        // We set DNS on the adapters ourselves (via WMI); strip it from the config so the engine does
        // not also try to (it only applies DNS for full tunnel anyway).
        config = WgConfigEditor.RemoveDns(config);

        var applied = false;
        if (redirectServers.Count > 0)
        {
            dns.Apply(name, redirectServers);
            // Drop entries resolved before the redirect so already-cached domains (e.g. a popular
            // youtube.com) are re-queried through the proxy and can be matched and routed, instead of
            // being served stale from the OS cache and silently bypassing split routing.
            dns.FlushCache();
            applied = true;
        }

        if (stripV6 && redirectServers.Count > 0)
        {
            // Give the IPv4-only tunnel adapter a working v4 resolver once it is up, so Windows' dead
            // fec0:: IPv6 servers (handed to a v4-only adapter) never stall lookups. Reapplied per run.
            _ = Task.Run(() => ConfigureTunnelAdapterDnsAsync(name, redirectServers));
        }

        // With the WebSocket transport the engine's endpoint is loopback; the connection that must stay
        // off the tunnel is wstunnel's own to the real server, so exclude the real server IP instead.
        var endpoint = useWebSocket ? wsServerIp : TunnelEndpoint.Resolve(config);
        var excluded = endpoint is not null && routes.AddEndpointExclusion(name, endpoint);

        // Full tunnel routes the default into the tunnel, which would swallow the local network too. Keep
        // the private LAN (RDP/SSH/printers, including a host one hop away in another local subnet)
        // reachable in parallel by routing the RFC1918 ranges out the physical gateway. Split mode never
        // tunnels the default, so the LAN is already direct and needs no exclusion. Added before the
        // adapter comes up so the next-hop resolves to the physical gateway, not the tunnel.
        var lanExcluded = !geoSplit && routes.AddLanExclusions(name, dualStack: !stripV6, exclusionCidrs);

        // The engine no longer arms its own kill-switch (we split the default route above), so when the
        // user opts into the kill-switch we arm our own once the tunnel adapter appears. The session
        // token lets teardown cancel a still-pending arm and guarantees the filters come down with the
        // tunnel.
        using var sessionCts = new CancellationTokenSource();

        // Always arm the WFP firewall once the tunnel adapter appears: it blocks QUIC (UDP/443) egressing
        // the tunnel so HTTP/3 falls back to TCP, which is reliable over the obfuscated tunnel (raw QUIC
        // stalls - e.g. some YouTube videos never load). The KILL-SWITCH (block everything off-tunnel) is a
        // full-tunnel concept: on in full tunnel, off in split (where it would sever the intended-direct
        // traffic). LAN bypass is always included; a dual-stack tunnel (config has a v6 Address) also gets
        // the v6 LAN bypass.
        var killSwitch = !geoSplit;
        // Under the kill-switch the WebSocket underlay (wstunnel.exe child) must be whitelisted by app-id,
        // or the block-all severs its TCP/TLS lifeline and the full tunnel passes no data.
        var underlayAppPath = useWebSocket ? TunnelPaths.WsTunnelExe() : null;
        _ = Task.Run(() => ArmFirewallAsync(name, killSwitch, !stripV6, underlayAppPath, exclusionCidrs, appSettings.BlockEncryptedDns, sessionCts.Token));

        // Re-flush the OS DNS cache once the tunnel is up. The connect-time flush above runs before the
        // adapter and its routes exist, so a name resolved in the window before the clean resolver's /32
        // route goes live can be answered from the local network's poisoned cache (a geo-blocked apex
        // like chatgpt.com gets a sinkhole IP that then sticks). Flushing again after the adapter appears
        // drops that window poison so the next lookup resolves cleanly through the tunnel.
        if (applied)
        {
            _ = Task.Run(() => FlushDnsWhenTunnelUpAsync(name, proxy, sessionCts.Token));
            // Pre-install routes for the rule's hostnames so an app with a pre-tunnel cached IP is tunnelled without a DNS query (#67).
            if (trackDomains && proxy is not null)
            {
                _ = Task.Run(() => SeedDomainRoutesAsync(name, proxy, sessionCts.Token));
            }
        }

        // Per-app tunneling (#68): watch which processes own which connections and route the matched apps'
        // remote IPs through the tunnel (DNS-path-independent, so it covers DoH/DoT apps). Feeds the shared
        // tracker created above; cancelled with the session on teardown.
        // App route watcher (TCP table) only when there are app matchers - it pins matched apps' TCP remote
        // IPs; the ETW UDP tracker below complements it for UDP.
        AppRouteWatcher? watcher = null;
        if (trackApps && tracker is not null)
        {
            var candidate = new AppRouteWatcher(tracker, apps, loggerFactory.CreateLogger<AppRouteWatcher>());
            if (candidate.HasMatchers)
            {
                watcher = candidate;
                _ = Task.Run(() => watcher.RunAsync(sessionCts.Token));
            }
        }

        // UDP complement (#77-udp): route UDP datagrams through the tunnel whose server IPs arrive via
        // signaling rather than DNS (e.g. Discord voice). Needed when tunneling matched apps' UDP (uses the
        // watcher's PID match) OR when wrapping ALL UDP (allUdp, no watcher). Its lifetime is the session
        // (stopped via sessionCts on teardown) - NOT a `using`, which would dispose it at end of scope and
        // race the Task.Run that has only just scheduled it. The underlay endpoint is excluded so the WG
        // transport never loops into the tunnel.
        if (tracker is not null && (watcher is not null || allUdp))
        {
            var udpTracker = new UdpFlowTracker(watcher, tracker, allUdp, endpoint, loggerFactory.CreateLogger<UdpFlowTracker>());
            _ = Task.Run(() => udpTracker.RunAsync(sessionCts.Token));
        }

        // Start the WebSocket transport last and redirect the endpoint to its loopback port. Done here,
        // with nothing between it and the engine start, so a started child is always reached by the
        // finally below (no orphaned wstunnel process). A start failure aborts the connect.
        if (useWebSocket)
        {
            wsTransport = await WsTunnelTransport.StartAsync(wsHost!, wsPort, wsTargetPort, wsPathPrefix, wsCredentials, loggerFactory.CreateLogger<WsTunnelTransport>(), CancellationToken.None);
            if (wsTransport is null)
            {
                throw new InvalidOperationException($"WebSocket transport (wstunnel) failed to start for {name}");
            }

            config = WgConfigEditor.SetEndpoint(config, $"127.0.0.1:{wsTransport.LocalPort}");

            // WSS carries the AmneziaWG UDP datagrams over TCP/TLS/WebSocket, adding ~90 bytes of
            // framing on top of WG's own ~80-byte overhead, and the TCP underlay cannot fragment. At
            // the default 1420 MTU an inner full-size segment (~1420) becomes ~1590 on the wire and
            // spans two physical 1500-byte segments, so a single underlay loss stalls/retransmits the
            // whole inner packet - the uncompensated-overhead gap behind the slow handshakes, choppy
            // media and stalled sends measured over WSS (#77). Clamp the tunnel MTU to 1280 (the IPv6
            // minimum, universally safe) while WSS is active so each inner segment fits one underlay
            // segment; with WSS off the config keeps its own MTU (default 1420).
            config = WgConfigEditor.SetMtu(config, WsTunnelMtu);
            logger.LogInformation("websocket transport active for {Name}: endpoint -> 127.0.0.1:{Port}, mtu -> {Mtu}", name, wsTransport.LocalPort, WsTunnelMtu);
        }

        try
        {
            WireGuardEngine.RunTunnelService(config, name);
        }
        finally
        {
            sessionCts.Cancel();
            firewall.Disable();

            if (wsTransport is not null)
            {
                await wsTransport.DisposeAsync();
            }

            if (applied)
            {
                dns.Restore();
                // Flush again so cached answers that resolved to tunnel-routed IPs do not survive the
                // tunnel and point at addresses no longer reachable off it.
                dns.FlushCache();
            }

            if (excluded)
            {
                routes.RemoveEndpointExclusion(name, endpoint!);
            }

            if (lanExcluded)
            {
                routes.RemoveLanExclusions(name);
            }
        }
    }

    private DnsProxy? StartProxy(IReadOnlyList<GeoDomain> domains, bool stripV6, IReadOnlyList<string> tunnelUpstream, IReadOnlyList<string> localUpstream, IReadOnlyList<string> lanUpstream, IReadOnlyList<string> localDomains, DomainTracker? tracker)
    {
        var tunnelIp = ParseFirst(tunnelUpstream, IPAddress.Parse("1.1.1.1"));
        var localIp = ParseFirst(localUpstream, tunnelIp);
        IPAddress? lanIp = lanUpstream.Count > 0 && IPAddress.TryParse(lanUpstream[0], out var li) ? li : null;
        var proxy = new DnsProxy(domains, tunnelIp, localIp, lanIp, localDomains, tracker, loggerFactory.CreateLogger<DnsProxy>(), stripV6);
        if (proxy.BoundV4 is null)
        {
            // Could not bind any loopback :53 (another resolver holds it). Degrade gracefully.
            return null;
        }

        var thread = new Thread(proxy.Serve)
        {
            IsBackground = true,
        };
        thread.Start();
        return proxy;
    }

    private static IPAddress ParseFirst(IReadOnlyList<string> servers, IPAddress fallback)
    {
        return servers.Count > 0 && IPAddress.TryParse(servers[0], out var ip) ? ip : fallback;
    }

    // Parses the user's "preferred DNS" setting (comma/space/semicolon separated) into valid IP literals,
    // dropping anything that is not a parseable address. Empty input yields an empty list (auto-detect).
    private static IReadOnlyList<string> ParseDnsServers(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return [.. value
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => IPAddress.TryParse(s, out _))];
    }

    // Splits the user exclusions list (newline / comma / semicolon / space separated) into IP/CIDR
    // entries (routed direct out the physical gateway in full tunnel) and domain entries (resolved by the
    // LAN resolver, split-DNS). A bare IP becomes a host route (/32 or /128); anything not parseable as an
    // address is treated as a domain suffix.
    private static (IReadOnlyList<string> Cidrs, IReadOnlyList<string> Domains) ParseExclusions(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return ([], []);
        }

        var cidrs = new List<string>();
        var domains = new List<string>();
        foreach (var token in value.Split(['\n', '\r', ',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var slash = token.IndexOf('/');
            var host = slash >= 0 ? token[..slash] : token;
            if (IPAddress.TryParse(host, out var ip))
            {
                var maxPrefix = ip.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;
                if (slash < 0)
                {
                    cidrs.Add($"{host}/{maxPrefix}"); // bare IP -> host route
                }
                else if (int.TryParse(token[(slash + 1)..], out var prefix) && prefix >= 0 && prefix <= maxPrefix)
                {
                    cidrs.Add($"{host}/{prefix}");
                }

                // else: a parseable IP with a malformed/out-of-range prefix (typo like 10.0.0.0/abc or /99)
                // is dropped - never reaches the route or firewall layer, so one typo can't disarm anything.
            }
            else
            {
                domains.Add(token);
            }
        }

        return (cidrs, domains);
    }

    /// <summary>
    /// Splits a wg-quick Endpoint value ("host:port") into host and port, or null when malformed. The
    /// last colon separates the port so IPv6 literals (rare for an Endpoint host) are not mis-split.
    /// </summary>
    private static (string Host, int Port)? ParseEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return null;
        }

        var colon = endpoint.LastIndexOf(':');
        if (colon <= 0 || colon == endpoint.Length - 1)
        {
            return null;
        }

        var host = endpoint[..colon].Trim();
        if (host.Length == 0
            || !int.TryParse(endpoint[(colon + 1)..].Trim(), System.Globalization.CultureInfo.InvariantCulture, out var port)
            || port is < 1 or > 65535)
        {
            return null;
        }

        return (host, port);
    }

    /// <summary>
    /// Resolves a host (or IPv4 literal) to its first IPv4 address, or null when unresolvable - used to
    /// exclude the wstunnel server from the tunnel so its TCP/TLS connection routes out the physical gateway.
    /// </summary>
    private static IPAddress? ResolveHostV4(string host)
    {
        if (IPAddress.TryParse(host, out var literal))
        {
            return literal.AddressFamily == AddressFamily.InterNetwork ? literal : null;
        }

        try
        {
            foreach (var address in Dns.GetHostAddresses(host))
            {
                if (address.AddressFamily == AddressFamily.InterNetwork)
                {
                    return address;
                }
            }
        }
        catch (SocketException)
        {
        }

        return null;
    }

    private static bool HasIpv6Address(string config)
    {
        foreach (var address in WgConfigEditor.GetAddresses(config))
        {
            if (address.Contains(':'))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Replaces a default route (0.0.0.0/0 or ::/0) with its two /1 halves, which together cover the
    /// same address space for routing but are not /0 - so the engine routes the full tunnel without
    /// arming its own kill-switch. Other prefixes pass through unchanged; the result is de-duplicated.
    /// </summary>
    private static IReadOnlyList<string> SplitDefaultRoutes(IReadOnlyList<string> allowedIps)
    {
        var result = new List<string>();

        static void AddUnique(List<string> list, string value)
        {
            if (!list.Contains(value))
            {
                list.Add(value);
            }
        }

        foreach (var cidr in allowedIps)
        {
            switch (cidr.Trim())
            {
                case "0.0.0.0/0":
                    AddUnique(result, "0.0.0.0/1");
                    AddUnique(result, "128.0.0.0/1");
                    break;
                case "::/0":
                    AddUnique(result, "::/1");
                    AddUnique(result, "8000::/1");
                    break;
                default:
                    AddUnique(result, cidr);
                    break;
            }
        }

        return result;
    }

    private async Task ConfigureTunnelAdapterDnsAsync(string name, IReadOnlyList<string> servers)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (routes.FindInterfaceIndex(name) is { } index)
            {
                dns.SetAdapter(index, servers);
                return;
            }

            await Task.Delay(500);
        }
    }

    /// <summary>
    /// Waits for the tunnel adapter (and its AllowedIPs routes, including the clean resolver's /32) to
    /// appear, then flushes the OS DNS cache - once when the adapter is up and once more after the first
    /// handshake settles. This drops any sinkhole answer cached during the bring-up window (before the
    /// clean resolver was routed through the tunnel), so a geo-blocked apex re-resolves cleanly instead
    /// of serving stale poison. Cancelled with the session if the tunnel is torn down first.
    /// </summary>
    private async Task FlushDnsWhenTunnelUpAsync(string name, DnsProxy? proxy, CancellationToken ct)
    {
        try
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
            while (DateTimeOffset.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                if (routes.FindInterfaceIndex(name) is not null)
                {
                    // Drop the proxy's OWN cache too (not just the OS cache): a matched geo-blocked name
                    // resolved in the bring-up window - before the clean resolver's /32 route was live -
                    // leaked to the poisoned local resolver and was cached here. Without clearing it the
                    // OS re-query is answered from the proxy's stale poison and the domain stays broken.
                    // Cleared at both flush points so the route-settle gap after the adapter appears is
                    // covered.
                    proxy?.ClearCache();
                    dns.FlushCache();
                    await Task.Delay(2000, ct);
                    proxy?.ClearCache();
                    dns.FlushCache();
                    return;
                }

                await Task.Delay(500, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Tunnel went down before it came up; nothing to flush.
        }
    }

    /// <summary>
    /// Waits for the tunnel adapter, then proactively resolves the rule's hostnames through the tunnel
    /// resolver and installs their /32 routes. Cancelled with the session if the tunnel is torn down first.
    /// </summary>
    private async Task SeedDomainRoutesAsync(string name, DnsProxy proxy, CancellationToken ct)
    {
        try
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
            while (DateTimeOffset.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                if (routes.FindInterfaceIndex(name) is not null)
                {
                    await proxy.SeedRoutesAsync(ct);
                    return;
                }

                await Task.Delay(500, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Waits for the tunnel adapter to appear, then arms the WFP firewall on it (QUIC block always, plus
    /// the kill-switch when <paramref name="killSwitch"/>). If the tunnel is torn down before or while
    /// arming (the session token is cancelled), nothing is left armed: the post-arm re-check disables it,
    /// and teardown's own Disable() is idempotent.
    /// </summary>
    private async Task ArmFirewallAsync(string name, bool killSwitch, bool dualStack, string? underlayAppPath, IReadOnlyList<string> extraLanCidrs, bool blockEncryptedDns, CancellationToken ct)
    {
        try
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
            while (DateTimeOffset.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                if (routes.FindInterfaceIndex(name) is { } index)
                {
                    firewall.Enable(index, killSwitch, dualStack, underlayAppPath, extraLanCidrs, blockEncryptedDns);
                    if (ct.IsCancellationRequested)
                    {
                        firewall.Disable();
                    }

                    return;
                }

                await Task.Delay(500, ct);
            }

            logger.LogWarning("firewall: tunnel adapter {Name} did not appear; not armed", name);
        }
        catch (OperationCanceledException)
        {
            // Tunnel went down before the adapter came up; nothing to arm.
        }
    }
}
