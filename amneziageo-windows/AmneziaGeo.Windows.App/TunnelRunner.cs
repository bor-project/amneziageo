using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using AmneziaGeo.Ipc;
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
    // Effective MTU when a config has no explicit value.
    private const int DefaultMtu = 1420;

    // Former default; a config storing exactly this follows DefaultMtu.
    private const int LegacyDefaultMtu = 1280;

    // Proactively refresh the peer handshake/NAT mapping so a lossy underlay can't let the session age out.
    private const int DefaultKeepaliveSeconds = 25;

    /// <summary>
    /// Runs the native tunnel service loop.
    /// </summary>
    public async Task RunAsync(string name)
    {
        try
        {
            await RunInnerAsync(name);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "connect {Name}: bring-up failed - {Reason}", name, ex.Message);
            // Bring-up can throw past the session teardown; the machine must never keep a kill-switch it can't reach.
            firewall.Disable();

            var reason = ex is ConnectFailureException cfe ? cfe.Reason : ConnectFailureReason.Unknown;
            try
            {
                await store.SetSettingAsync(TunnelPaths.ConnectMessageKey(name), ex.Message);
                await store.SetSettingAsync(TunnelPaths.ConnectReasonKey(name), reason.ToString());
            }
            catch
            {
            }

            throw;
        }
    }

    private async Task RunInnerAsync(string name)
    {
        var connectSw = Stopwatch.StartNew();
        logger.LogInformation("connect {Name}: bring-up starting", name);

        using (logger.Step("reconcile leftovers"))
        {
            reconciler.Reconcile();
        }

        var config = await store.GetConfigTextAsync(name)
            ?? throw new ConnectFailureException(ConnectFailureReason.ConfigMissing, $"configuration '{name}' is not stored");
        // Log length only - the config carries private keys.
        logger.LogTrace("connect {Name}: config loaded ({Length} chars) [{Elapsed} ms]", name, config.Length, connectSw.ElapsedMilliseconds);

        // Resolve the WS transport up front; start wstunnel last so a setup failure can't orphan it.
        var transport = await store.GetConfigTransportAsync(name);
        var useWebSocket = transport?.UseWebSocket == true;

        var storedMtu = transport?.Mtu ?? 0;
        var effectiveMtu = storedMtu > 0 && storedMtu != LegacyDefaultMtu ? storedMtu : DefaultMtu;
        string? wsHost = null;
        var wsPort = 0;
        var wsTargetPort = 0;
        var wsPathPrefix = string.Empty;
        var wsCredentials = string.Empty;
        IPAddress? wsServerIp = null;
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
                // WebSocketHost may be a full wss:// URL; resolve its host for the exclusion route.
                var ws = WsEndpoint.Parse(transport!.WebSocketHost, transport.WebSocketPort, endpointHost);
                wsHost = ws.Host;
                wsPort = ws.Port;
                wsPathPrefix = ws.PathPrefix;
                wsCredentials = ws.Credentials;
                wsServerIp = ResolveHostV4(wsHost);
            }
        }

        if (useWebSocket)
        {
            // Log only that a path token is set, never its value - path/credentials are secrets.
            logger.LogDebug("connect {Name}: websocket underlay -> {Host}:{Port} pathToken={HasPath} targetPort={Target}",
                name, wsHost, wsPort, !string.IsNullOrEmpty(wsPathPrefix), wsTargetPort);
        }

        WsTunnelTransport? wsTransport = null;

        var geo = await store.GetActiveTunnelGeoAsync(name);

        var geoRoutes = new List<string>(geo?.Routes ?? []);
        var domains = geo?.Domains ?? [];
        var apps = geo?.Apps ?? [];

        // Routing list owns DNS/exclusions/AllUdp/IPv6/global-proxy when assigned; else per-config defaults.
        // Loaded early because the IPv6 opt-in below gates the v6-strip, and the global-proxy flag decides split.
        var activeRoutingListId = await store.GetActiveRoutingListIdAsync(name);
        var routingSettings = activeRoutingListId is long activeListId
            ? await store.GetRoutingSettingsAsync(activeListId)
            : null;
        // The assigned list's Direct/Block buckets (its Proxy bucket already rode the projection into geo).
        var activeList = activeRoutingListId is long bucketListId
            ? await store.GetRoutingListAsync(bucketListId)
            : null;

        // Global proxy on = full tunnel minus the Direct bucket; off = split (tunnel only the Proxy bucket). A
        // routing list's flag wins over the config's own split; without a list the config's split stands.
        var geoSplit = activeList is not null
            ? !(routingSettings?.UseGlobalProxy ?? false)
            : (geo?.GeoSplit ?? false);

        logger.LogDebug("connect {Name}: geo loaded - split={Split} routes={Routes} domains={Domains} apps={Apps} [{Elapsed} ms]",
            name, geoSplit, geoRoutes.Count, domains.Count, apps.Count, connectSw.ElapsedMilliseconds);

        // Block bucket applies always: WFP drops the CIDRs, the DNS proxy refuses the domains (NXDOMAIN).
        var blockRoutes = activeList?.BlockRoutes ?? [];
        var blockDomains = activeList?.BlockDomains ?? [];

        // Route IPv6 only when the active routing list opts in (#149); otherwise the tunnel stays v4-only:
        // AAAA is answered NODATA so clients fall back to A, and the adapter carries no IPv6 address or routes.
        // A partial dual stack would open a v6 leak/blackhole, so the whole v6 path is gated on this one flag.
        var stripV6 = !(routingSettings?.UseIpv6 ?? false);

        // Domain tracking only in split mode.
        var trackDomains = geoSplit && domains.Count > 0;

        // App tracking only in split mode.
        var trackApps = geoSplit && apps.Count > 0;

        // Tunnel resolver = config DNS, reached through the tunnel; add its /32 to routes. The proxy forwards
        // to the tunnel resolver over an IPv4 socket, so an IPv6 resolver (e.g. Cloudflare's
        // 2606:4700:4700::1111, shipped by many configs alongside 1.1.1.1) is unreachable - every failover to it
        // fails instantly with "address incompatible with the requested protocol", turning a single dropped
        // primary datagram into a hard DNS failure. Keep IPv4 resolvers only; the fallback below tops the list
        // up to a distinct pair.
        var configDns = WgConfigEditor.GetDns(config)
            .Where(d => IPAddress.TryParse(d, out var dip) && dip.AddressFamily == AddressFamily.InterNetwork)
            .ToList();
        var resolvers = configDns.Count > 0 ? new List<string>(configDns) : new List<string> { "1.1.1.1" };
        // Ensure a distinct secondary resolver so DNS survives a resolver blackhole (failover),
        // not just an occasional dropped datagram (retransmit). Each /32 is routed below.
        foreach (var fallback in new[] { "1.1.1.1", "1.0.0.1" })
        {
            if (resolvers.Count >= 2)
            {
                break;
            }

            if (!resolvers.Contains(fallback))
            {
                resolvers.Add(fallback);
            }
        }

        IReadOnlyList<string> tunnelResolver = resolvers;
        // Resolver /32s are infrastructure: routed through the tunnel so the tunnel DNS stays reachable. Collect
        // them so they can be excluded from the reconcilable list set below - a list range that happens to equal a
        // resolver IP must never be torn down by the live reconcile, or DNS through the tunnel dies.
        var resolverRoutes = new HashSet<string>(StringComparer.Ordinal);
        if (trackDomains)
        {
            foreach (var server in tunnelResolver)
            {
                if (!IPAddress.TryParse(server, out _))
                {
                    continue;
                }

                var route = $"{server}/32";
                resolverRoutes.Add(route);
                if (!geoRoutes.Contains(route))
                {
                    geoRoutes.Add(route);
                }
            }
        }

        // Reconcilable list ranges = the list's own ranges MINUS resolver infrastructure, so a range that
        // coincides with a tunnel-DNS resolver /32 stays advertised (in _staticRoutes) but is never in _listRoutes.
        var listRoutes = (geo?.Routes ?? []).Where(r => !resolverRoutes.Contains(r)).ToList();

        var allowedIps = AllowedIpsResolver.Build(geoSplit, WgConfigEditor.GetAllowedIps(config), geoRoutes);
        if (stripV6)
        {
            // v4-only tunnel: strip IPv6 routes and the IPv6 interface Address so the adapter is purely v4
            // (a dangling v6 adapter address with no v6 routes is exactly the blackhole this mode avoids).
            allowedIps = [.. allowedIps.Where(a => !a.Contains(':'))];
            config = WgConfigEditor.StripIpv6Addresses(config);
        }

        // Split /0 into /1 halves so the engine's blanket kill-switch isn't armed.
        allowedIps = SplitDefaultRoutes(allowedIps);

        config = WgConfigEditor.ApplyAllowedIps(config, allowedIps);
        logger.LogDebug("connect {Name}: allowed-ips resolved - {Count} entries, mtu={Mtu}, websocket={Ws} [{Elapsed} ms]",
            name, allowedIps.Count, effectiveMtu, useWebSocket, connectSw.ElapsedMilliseconds);

        var appSettings = await settings.LoadAsync();

        // All-UDP catch-all (split-only); from the routing list or the global setting.
        var allUdp = geoSplit && (routingSettings?.AllUdp ?? appSettings.TunnelAllUdp);

        // Preferred-DNS overrides the system resolvers for non-tunneled names; empty = auto-detect.
        var preferredDnsServers = (await store.GetConfigDnsAsync(name))?.Servers ?? string.Empty;
        var preferredDns = ParseDnsServers(preferredDnsServers);
        // LAN resolver always captured; local names resolve here, not offshore.
        var lanResolvers = dns.CaptureUpstream();
        var upstream = preferredDns.Count > 0 ? preferredDns : lanResolvers;
        // Bypass list: routing list's exclusions or per-config. The LAN floor is added unconditionally below.
        string? storedExclusions;
        if (routingSettings is not null)
        {
            storedExclusions = routingSettings.Exclusions;
        }
        else
        {
            var configExclusions = await store.GetConfigExclusionsAsync(name);
            storedExclusions = configExclusions?.Exclusions;
        }

        var (parsedCidrs, parsedExclusionDomains) = ParseExclusions(storedExclusions ?? string.Empty);
        var exclusionDomains = new List<string>(parsedExclusionDomains);
        // Direct bucket (both modes): its domains stay on the local resolver, off the tunnel, overriding a proxy match.
        if (activeList is not null)
        {
            foreach (var direct in activeList.DirectDomains)
            {
                if (!exclusionDomains.Contains(direct.Value))
                {
                    exclusionDomains.Add(direct.Value);
                }
            }
        }
        // Keep LAN DNS suffixes off-tunnel (split-horizon DNS).
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
        // Resolve the wstunnel host via the LAN resolver - the tunnel isn't up yet.
        if (useWebSocket && wsHost is not null && !IPAddress.TryParse(wsHost, out _))
        {
            exclusionDomains.Add(wsHost);
        }

        // Plain-UDP endpoint: pin it to an IP resolved via the still-clean LAN resolver so the engine does no
        // DNS at bring-up - full tunnel would otherwise resolve the host through the not-yet-up tunnel and die
        // with "No such host". The stored config keeps the hostname; only this in-memory copy carries the IP.
        // Keep the host on-LAN too, so if it stays a hostname the engine resolves it off-tunnel.
        if (!useWebSocket && ParseEndpoint(WgConfigEditor.GetEndpoint(config)) is { } endpointParts
            && !IPAddress.TryParse(endpointParts.Host, out _))
        {
            if (!exclusionDomains.Contains(endpointParts.Host))
            {
                exclusionDomains.Add(endpointParts.Host);
            }

            var pinnedIp = await PinEndpointAsync(name, endpointParts.Host);
            if (pinnedIp is not null)
            {
                config = WgConfigEditor.SetEndpoint(config, $"{pinnedIp}:{endpointParts.Port}");
                logger.LogInformation("endpoint {Host} pinned to {Ip} (pre-tunnel resolve)", endpointParts.Host, pinnedIp);
            }
            else
            {
                logger.LogWarning("endpoint {Host} unresolved pre-tunnel; engine resolves it via LAN", endpointParts.Host);
            }
        }
        // Bypass floor = RFC1918 + connected subnets, always: a full tunnel with the kill-switch must never
        // blackhole the local LAN, and a split tunnel honours the same manual list. Stored exclusions add to
        // the floor, they never replace it.
        var exclusionCidrs = new List<string>(routes.DefaultExclusionEntries());
        foreach (var cidr in parsedCidrs)
        {
            if (!exclusionCidrs.Contains(cidr))
            {
                exclusionCidrs.Add(cidr);
            }
        }

        // Direct bucket (both modes): its CIDRs route out the physical gateway, bypassing the tunnel, overriding a proxy route.
        if (activeList is not null)
        {
            foreach (var cidr in activeList.DirectRoutes)
            {
                if (!exclusionCidrs.Contains(cidr))
                {
                    exclusionCidrs.Add(cidr);
                }
            }
        }

        IReadOnlyList<string> redirectServers = [];

        var localResolver = geoSplit
            ? (upstream.Count > 0 ? upstream : tunnelResolver)
            : tunnelResolver;

        // One tracker shared by DNS and app paths; created before the tracker starts so its loop stops with the session.
        using var sessionCts = new CancellationTokenSource();

        // Tracker when there's live work or a routing list drives the split.
        DomainTracker? tracker = null;
        if (trackDomains || trackApps || allUdp || (geoSplit && activeRoutingListId is not null))
        {
            var peer = WgConfigEditor.GetPeerPublicKey(config);
            if (peer is not null)
            {
                // Started after the geo-domain sink is attached to avoid a rebuild race.
                tracker = new DomainTracker(store, routes, uapi, loggerFactory.CreateLogger<DomainTracker>(), name, peer, geoRoutes, listRoutes, domains, appSettings.RefreshSeconds, stripV6);
            }
        }

        var proxy = StartProxy(trackDomains ? domains : [], blockDomains, stripV6, geoSplit, tunnelResolver, localResolver, lanResolvers, exclusionDomains, tracker);

        // Rebuild the proxy matcher live on a geosite refresh or list rule edit, even for a list that had no
        // domains at connect. A rebuild that adds domains flushes the OS resolver cache so a name resolved
        // direct before the edit is re-queried through the proxy and re-pointed onto the tunnel.
        if (geoSplit && proxy is not null && tracker is not null)
        {
            tracker.SetGeoDomainSink((d, ct) =>
            {
                if (proxy.UpdateDomains(d, ct))
                {
                    dns.FlushCache();
                }
            });
        }

        if (tracker is not null)
        {
            _ = Task.Run(() => tracker.RunAsync(sessionCts.Token));
        }

        if (proxy?.BoundV4 is not null)
        {
            redirectServers = [proxy.BoundV4.ToString()];
        }
        else
        {
            // Loopback :53 busy - fall back to direct resolvers.
            redirectServers = configDns.Count > 0 ? configDns : upstream;
            logger.LogWarning("DNS proxy unavailable (loopback :53 busy); using direct resolvers");
        }

        logger.LogDebug("connect {Name}: dns proxy {State}, tracker={Tracker} [{Elapsed} ms]",
            name, proxy?.BoundV4 is not null ? $"bound {proxy.BoundV4}" : "unavailable", tracker is not null, connectSw.ElapsedMilliseconds);

        // Strip DNS from config; we apply it on the adapter ourselves.
        config = WgConfigEditor.RemoveDns(config);

        var applied = false;
        if (redirectServers.Count > 0)
        {
            using (logger.Step("apply DNS + flush cache"))
            {
                dns.Apply(name, redirectServers);
                // Flush so pre-redirect answers are re-queried through the proxy.
                dns.FlushCache();
            }

            applied = true;
            logger.LogDebug("connect {Name}: dns redirected to {Servers} [{Elapsed} ms]",
                name, string.Join(",", redirectServers), connectSw.ElapsedMilliseconds);
        }

        if (stripV6 && redirectServers.Count > 0)
        {
            // Set a v4 resolver on the v4-only adapter.
            _ = Task.Run(() => ConfigureTunnelAdapterDnsAsync(name, redirectServers));
        }

        // Exclude wstunnel's real server IP, not the loopback endpoint.
        var endpoint = useWebSocket ? wsServerIp : TunnelEndpoint.Resolve(config);
        var excluded = endpoint is not null && routes.AddEndpointExclusion(name, endpoint);

        // Keep the LAN and any manual exclusions direct in both modes (RFC1918 floor + stored list). In split
        // mode the tunnelled geo routes are more specific, so longest-prefix-match keeps them on the tunnel.
        var lanExcluded = routes.AddLanExclusions(name, dualStack: !stripV6, exclusionCidrs);
        logger.LogDebug("connect {Name}: routes - endpoint {Endpoint} excluded={Excluded}, lan-exclusions={Lan} [{Elapsed} ms]",
            name, endpoint?.ToString() ?? "none", excluded, lanExcluded, connectSw.ElapsedMilliseconds);

        // WFP kill-switch: on in full tunnel, off in split. QUIC follows the same routes as TCP.
        var killSwitch = !geoSplit;
        // Whitelist wstunnel under the kill-switch.
        var underlayAppPath = useWebSocket ? TunnelPaths.WsTunnelExe() : null;
        _ = Task.Run(() => ArmFirewallAsync(name, killSwitch, !stripV6, underlayAppPath, exclusionCidrs, blockRoutes, sessionCts.Token));

        // Re-flush after the adapter appears to drop bring-up-window poison.
        if (applied)
        {
            _ = Task.Run(() => FlushDnsWhenTunnelUpAsync(name, proxy, sessionCts.Token));
            // Lazy model: nothing is pre-resolved or restored at connect (no DB warm start). The in-memory
            // rule-backed cache is populated purely on demand per DNS query; a matched name resolves through
            // the tunnel resolver on first use and self-heals (re-resolve + evict) while actively used. This
            // also avoids the old "seed storm" that saturated the tunnel DNS path at connect.
            // (DnsProxy.SeedRoutesAsync is kept for easy revert but intentionally not invoked.)
        }

        // App route watcher pins matched apps' TCP remote IPs into the tracker.
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

        // UDP tracker: routes UDP by signaling (not DNS); not a using to avoid racing Task.Run.
        if (tracker is not null && (watcher is not null || allUdp))
        {
            var udpTracker = new UdpFlowTracker(watcher, tracker, allUdp, endpoint, loggerFactory.CreateLogger<UdpFlowTracker>());
            _ = Task.Run(() => udpTracker.RunAsync(sessionCts.Token));
        }

        // Start wstunnel last so a failure can't orphan it.
        if (useWebSocket)
        {
            wsTransport = await WsTunnelTransport.StartAsync(wsHost!, wsPort, wsTargetPort, wsPathPrefix, wsCredentials, loggerFactory.CreateLogger<WsTunnelTransport>(), CancellationToken.None);
            if (wsTransport is null)
            {
                throw new ConnectFailureException(ConnectFailureReason.UnderlayUnreachable, $"WebSocket transport (wstunnel) failed to start for {name}");
            }

            config = WgConfigEditor.SetEndpoint(config, $"127.0.0.1:{wsTransport.LocalPort}");
            logger.LogInformation("websocket transport active for {Name}: endpoint -> 127.0.0.1:{Port}", name, wsTransport.LocalPort);
        }

        config = WgConfigEditor.SetMtu(config, effectiveMtu);
        // Keep the peer handshake/NAT state warm so a lossy underlay doesn't let the session age out into a
        // forced re-dial; only injected when the imported config didn't already specify its own keepalive.
        config = WgConfigEditor.EnsurePersistentKeepalive(config, DefaultKeepaliveSeconds);
        logger.LogInformation("mtu for {Name}: {Mtu}, keepalive ensured ({Keepalive}s)", name, effectiveMtu, DefaultKeepaliveSeconds);

        logger.LogInformation("connect {Name}: bring-up complete in {Elapsed} ms, starting engine", name, connectSw.ElapsedMilliseconds);

        try
        {
            WireGuardEngine.RunTunnelService(config, name);
        }
        catch (Exception ex) when (ex is not ConnectFailureException)
        {
            throw new ConnectFailureException(ConnectFailureReason.AdapterStartFailed, ex.Message, ex);
        }
        finally
        {
            logger.LogInformation("connect {Name}: session ended after {Elapsed} ms, tearing down", name, connectSw.ElapsedMilliseconds);
            // Cancel before disabling: arming re-checks the token after Enable, so a late arm undoes itself.
            sessionCts.Cancel();
            firewall.Disable();

            if (wsTransport is not null)
            {
                await wsTransport.DisposeAsync();
            }

            if (applied)
            {
                dns.Restore();
                // Flush so cached tunnel-routed IPs don't outlive the tunnel.
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

    private DnsProxy? StartProxy(IReadOnlyList<GeoDomain> domains, IReadOnlyList<GeoDomain> blockDomains, bool stripV6, bool localIsLan, IReadOnlyList<string> tunnelUpstream, IReadOnlyList<string> localUpstream, IReadOnlyList<string> lanUpstream, IReadOnlyList<string> localDomains, DomainTracker? tracker)
    {
        var tunnelIp = ParseFirst(tunnelUpstream, IPAddress.Parse("1.1.1.1"));
        var tunnelSecondary = tunnelUpstream.Count > 1 && IPAddress.TryParse(tunnelUpstream[1], out var ts) ? ts : null;
        var localIp = ParseFirst(localUpstream, tunnelIp);
        IPAddress? lanIp = lanUpstream.Count > 0 && IPAddress.TryParse(lanUpstream[0], out var li) ? li : null;
        var lanPool = lanUpstream
            .Select(s => IPAddress.TryParse(s, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork ? ip : null)
            .Where(ip => ip is not null)
            .Select(ip => ip!)
            .ToList();
        var proxy = new DnsProxy(domains, blockDomains, tunnelIp, localIp, lanIp, lanPool, localIsLan, localDomains, tracker, loggerFactory.CreateLogger<DnsProxy>(), stripV6, tunnelSecondary);
        if (proxy.BoundV4 is null)
        {
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

                // Malformed prefix is dropped.
            }
            else
            {
                domains.Add(token);
            }
        }

        return (cidrs, domains);
    }

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

    /// <summary>
    /// Resolves the endpoint host to an IPv4 in the clean pre-tunnel context, retrying a cold flap, and falls
    /// back to the last-known-good IP; persists a fresh resolve as the new last-known-good.
    /// </summary>
    private async Task<IPAddress?> PinEndpointAsync(string name, string host)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var live = await ResolveHostV4Async(host);
            if (live is not null)
            {
                try
                {
                    await store.SetSettingAsync(TunnelPaths.EndpointIpKey(name), live.ToString());
                }
                catch
                {
                }

                return live;
            }

            await Task.Delay(400);
        }

        var cached = await ReadCachedEndpointAsync(name);
        if (cached is not null)
        {
            logger.LogInformation("endpoint {Host} using last-known-good {Ip}", host, cached);
        }

        return cached;
    }

    private static async Task<IPAddress?> ResolveHostV4Async(string host)
    {
        try
        {
            foreach (var address in await Dns.GetHostAddressesAsync(host))
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

    private async Task<IPAddress?> ReadCachedEndpointAsync(string name)
    {
        try
        {
            var stored = await store.GetSettingAsync(TunnelPaths.EndpointIpKey(name));
            if (stored is not null && IPAddress.TryParse(stored, out var ip)
                && ip.AddressFamily == AddressFamily.InterNetwork)
            {
                return ip;
            }
        }
        catch
        {
        }

        return null;
    }

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
                    // Clear the proxy cache too - bring-up-window poison lingers here.
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
        }
    }

    private async Task ArmFirewallAsync(string name, bool killSwitch, bool dualStack, string? underlayAppPath, IReadOnlyList<string> extraLanCidrs, IReadOnlyList<string> blockCidrs, CancellationToken ct)
    {
        try
        {
            var index = await WaitForAdapterAsync(name, ct);
            if (index is null)
            {
                logger.LogWarning("firewall: tunnel adapter {Name} did not appear; not armed", name);
                return;
            }

            if (killSwitch)
            {
                // Block-list drops are user intent and go up with the adapter; only the kill-switch waits.
                if (blockCidrs.Count > 0 && !Arm(index.Value, killSwitch: false, dualStack, underlayAppPath, extraLanCidrs, blockCidrs, ct))
                {
                    return;
                }

                // The kill-switch protects an established tunnel, not the dial: a server that never answers
                // would otherwise firewall the machine off for the whole attempt (#208).
                logger.LogDebug("firewall: kill-switch for {Name} deferred until the first handshake", name);
                await WaitForHandshakeAsync(name, ct);
            }

            Arm(index.Value, killSwitch, dualStack, underlayAppPath, extraLanCidrs, blockCidrs, ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "firewall: arming failed for {Name}", name);
        }
    }

    // Installs the filters and returns whether they survived. The session cancels before the teardown disables,
    // so a set that lands after it undoes itself here.
    private bool Arm(uint index, bool killSwitch, bool dualStack, string? underlayAppPath, IReadOnlyList<string> extraLanCidrs, IReadOnlyList<string> blockCidrs, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        firewall.Enable(index, killSwitch, dualStack, underlayAppPath, extraLanCidrs, blockCidrs);
        if (ct.IsCancellationRequested)
        {
            firewall.Disable();
            return false;
        }

        return true;
    }

    // Returns the tunnel interface index, or null when the adapter never appears.
    private async Task<uint?> WaitForAdapterAsync(string name, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (routes.FindInterfaceIndex(name) is { } index)
            {
                return index;
            }

            await Task.Delay(500, ct);
        }

        return null;
    }

    // Waits for the peer to answer. No deadline: the session token ends the wait when the attempt is torn down.
    private async Task WaitForHandshakeAsync(string name, CancellationToken ct)
    {
        while (uapi.TryGetLastHandshake(name) is not > 0)
        {
            await Task.Delay(500, ct);
        }
    }
}
