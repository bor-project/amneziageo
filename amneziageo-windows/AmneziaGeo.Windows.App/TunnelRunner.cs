using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using AmneziaGeo.Ipc;
using AmneziaGeo.Routing;
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
    LiveSession session,
    RuntimeInspector inspector,
    ILoggerFactory loggerFactory,
    ILogger<TunnelRunner> logger)
{
    // Effective MTU when a config has no explicit value.

    // Proactively refresh the peer handshake/NAT mapping so a lossy underlay can't let the session age out.
    internal const int DefaultKeepaliveSeconds = 25;

    // How long the leftover cleanup may hold bring-up: several times what it costs on a busy machine, and a
    // small share of the 30s the service manager allows a service to report running.
    private static readonly TimeSpan _reconcileBudget = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Resolvers the config declares, IPv4 only.
    /// </summary>
    internal static IReadOnlyList<string> ConfigResolvers(string config)
    {
        // The proxy forwards to the tunnel resolver over an IPv4 socket, so an IPv6 resolver (e.g. Cloudflare's
        // 2606:4700:4700::1111, shipped by many configs alongside 1.1.1.1) is unreachable - every failover to it
        // fails instantly with "address incompatible with the requested protocol", turning a single dropped
        // primary datagram into a hard DNS failure.
        return [.. WgConfigEditor.GetDns(config)
            .Where(d => IPAddress.TryParse(d, out var dip) && dip.AddressFamily == AddressFamily.InterNetwork)];
    }

    /// <summary>
    /// Resolvers reached through the tunnel: the config's own, topped up to a distinct pair so DNS survives a
    /// blackholed resolver, not just a dropped datagram.
    /// </summary>
    internal static IReadOnlyList<string> TunnelResolvers(IReadOnlyList<string> configDns)
    {
        var resolvers = configDns.Count > 0 ? new List<string>(configDns) : new List<string> { "1.1.1.1" };
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

        return resolvers;
    }

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
            logger.LogError(ex, "{Name}: setting up the connection failed ({Reason}); the firewall protection is lifted so the machine keeps working, and nothing goes through the tunnel", name, ex.Message);
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
        logger.LogInformation("{Name}: setting up the connection", name);

        // Bound the cleanup: it runs inside the service manager's start deadline, and a WMI call hung behind a
        // restarting service holds it past that deadline, so the tunnel never reports running at all (#247).
        // Whatever is left behind the agent reverts on its own pass.
        using (logger.Step("reconcile leftovers"))
        {
            var cleanup = Task.Run(() => reconciler.Reconcile());
            if (await Task.WhenAny(cleanup, Task.Delay(_reconcileBudget)).ConfigureAwait(false) != cleanup)
            {
                logger.LogWarning("cleaning up after an earlier session has taken more than {Sec}s, so the tunnel comes up without waiting for it; leftover routes or DNS settings are reverted on a later pass",
                    (int)_reconcileBudget.TotalSeconds);
            }
        }

        var config = await store.GetConfigTextAsync(name)
            ?? throw new ConnectFailureException(ConnectFailureReason.ConfigMissing, $"configuration '{name}' is not stored");
        // Log length only - the config carries private keys.
        logger.LogTrace("{Name}: configuration read, {Length} chars [{Elapsed} ms in]", name, config.Length, connectSw.ElapsedMilliseconds);

        // Resolve the WS transport up front; start wstunnel last so a setup failure can't orphan it.
        var transport = await store.GetConfigTransportAsync(name);
        var useWebSocket = transport?.UseWebSocket == true;

        var effectiveMtu = MtuPlan.ResolveForLink(transport?.MtuMode ?? MtuMode.Auto, transport?.Mtu ?? 0, config);
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
                logger.LogWarning("{Name} asks for the websocket carrier but its Endpoint is missing or malformed; connecting over plain UDP instead, which a filtering provider may block", name);
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
            logger.LogDebug("{Name}: the tunnel will be carried inside a websocket to {Host}:{Port} (path token set: {HasPath}) and handed to port {Target} on the server",
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

        logger.LogDebug("{Name}: rules loaded — {Routes} address range(s), {Domains} domain(s), {Apps} app(s); only what they name goes through the tunnel: {Split} [{Elapsed} ms in]",
            name, geoRoutes.Count, domains.Count, apps.Count, geoSplit, connectSw.ElapsedMilliseconds);

        // Block bucket applies always: WFP drops the CIDRs, the DNS proxy refuses the domains (NXDOMAIN).
        var blockRoutes = activeList?.BlockRoutes ?? [];
        var blockDomains = activeList?.BlockDomains ?? [];

        // WFP kill-switch: on in both modes. In split it holds a destination off the physical path until its
        // verdict exists - a packet that leaves earlier carries the real address, which is the leak this prevents.
        // Resolved here because the on-demand router needs it before the DNS proxy starts.
        const bool killSwitch = true;

        // Every bucket is resolved per destination, whatever its size: nothing is materialized at bring-up.
        var listDirect = activeList?.DirectRoutes ?? [];

        // Route IPv6 only when the config opts in (ConfigTransport.UseIpv6); otherwise the tunnel stays v4-only:
        // AAAA is answered NODATA so clients fall back to A, and the adapter carries no IPv6 address or routes.
        // A partial dual stack would open a v6 leak/blackhole, so the whole v6 path is gated on this one flag.
        // The config owns this because the server behind it may or may not have an IPv6 address.
        var stripV6 = !(transport?.UseIpv6 ?? false);

        // Domain tracking only in split mode.
        var trackDomains = geoSplit && domains.Count > 0;

        // App tracking only in split mode.
        var trackApps = geoSplit && apps.Count > 0;

        // Tunnel resolver = config DNS, reached through the tunnel; add its /32 to routes.
        var configDns = ConfigResolvers(config);
        var tunnelResolver = TunnelResolvers(configDns);
        // Resolver /32s are infrastructure: routed through the tunnel so the tunnel DNS stays reachable. Collect
        // them so they can be excluded from the reconcilable list set below - a list range that happens to equal a
        // resolver IP must never be torn down by the live reconcile, or DNS through the tunnel dies.
        var resolverRoutes = new HashSet<string>(StringComparer.Ordinal);
        if (geoSplit)
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

        // Split starts empty: only the resolver infrastructure is advertised, and a proxy destination earns its
        // /32 on contact. Materializing a geo database up front is what put thousands of routes on the adapter.
        var startupRoutes = geoSplit ? resolverRoutes.ToList() : geoRoutes;
        var allowedIps = AllowedIpsResolver.Build(geoSplit, WgConfigEditor.GetAllowedIps(config), startupRoutes);
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
        logger.LogDebug("{Name}: the tunnel will accept {Count} address range(s), packet size {Mtu}, carried in a websocket: {Ws} [{Elapsed} ms in]",
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
        // Direct bucket (both modes): its domains stay on the local resolver, off the tunnel, overriding a proxy
        // match. Handed to the proxy on its own so an edited list rebuilds it without a fresh tunnel.
        var directDomains = activeList?.DirectDomains ?? [];
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
            logger.LogInformation("names ending in {Suffixes} belong to your own network, so they keep resolving there and never go through the tunnel", string.Join(", ", exclusionDomains.Skip(parsedExclusionDomains.Count)));
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
                logger.LogInformation("the server name {Host} was resolved to {Ip} before the tunnel came up and is held there, so it stays reachable once DNS moves into the tunnel", endpointParts.Host, pinnedIp);
            }
            else
            {
                logger.LogWarning("the server name {Host} could not be resolved before the tunnel came up; the connection will try to resolve it itself and may fail with 'no such host'", endpointParts.Host);
            }
        }

        // Probe for the underlay hop. Read from the endpoint because it keeps an explicit off-tunnel route, so the
        // lookup still answers with the physical gateway after the tunnel's default halves are installed.
        var underlayProbe = useWebSocket ? wsServerIp : TunnelEndpoint.Resolve(config);

        // Bypass floor = the connected subnets, always: a full tunnel with the kill-switch must never
        // blackhole the local LAN, and a split tunnel honours the same manual list. Stored exclusions add to
        // the floor, they never replace it.
        var exclusionCidrs = new List<string>(routes.DefaultExclusionEntries());
        // A geoip country rule brings tens of thousands of entries; a scan per entry is quadratic.
        var seenCidrs = new HashSet<string>(exclusionCidrs, StringComparer.Ordinal);
        foreach (var cidr in parsedCidrs)
        {
            if (seenCidrs.Add(cidr))
            {
                exclusionCidrs.Add(cidr);
            }
        }

        // The Direct bucket is not materialized here at all: each of its prefixes becomes a host route on contact.

        // Adjacent prefixes fold into one another, cutting both the route table and the WFP filter set.
        var bypassCidrs = CidrAggregator.Aggregate(exclusionCidrs);
        if (bypassCidrs.Count < exclusionCidrs.Count)
        {
            logger.LogInformation("{From} address ranges kept out of the tunnel were merged into {To}, which shortens the route table and the firewall rules", exclusionCidrs.Count, bypassCidrs.Count);
        }

        IReadOnlyList<string> redirectServers = [];

        var localResolver = geoSplit
            ? (upstream.Count > 0 ? upstream : tunnelResolver)
            : tunnelResolver;

        // One tracker shared by DNS and app paths; created before the tracker starts so its loop stops with the session.
        using var sessionCts = new CancellationTokenSource();

        // Verdict path for a database-sized Direct bucket: nothing is installed at connect, and a destination earns
        // its host route on first contact - from a DNS answer, or from an ETW connect event when it never resolved.
        // The proxy bucket is passed too: it decides precedence on an overlap, and in split it marks the addresses a
        // proxy range would otherwise pull into the tunnel. Built before the tracker, which consults it before
        // routing a resolved name into the tunnel.
        var synReset = new SynSentReset(name, loggerFactory.CreateLogger<SynSentReset>());
        var applier = new RouteApplier(
            routes,
            firewall,
            uapi,
            name,
            WgConfigEditor.GetPeerPublicKey(config),
            () => underlayProbe is null ? (null, 0u) : RouteManager.UnderlayHop(underlayProbe),
            killSwitch,
            synReset);
        // The probe attributes each live connection to its process, so a matched app's destination is never taken
        // for ordinary traffic and settled onto the physical path.
        var liveDestinations = new TcpTableProbe();
        // The resolver addresses are handed over as pinned: a list range that covers one would otherwise make the
        // cache own its route and reclaim it as idle, because the agent's own queries are attributed to no process.
        // Nothing an earlier session held survives into this one: its verdicts were taken under the list of its own
        // time, and its routes would outlive the interface they were installed on.
        if (session.Cache is { Size: > 0 } previous)
        {
            logger.LogInformation("{Name}: {Count} address(es) held by the previous session are released; this one decides every destination again by the list in force now", name, previous.Size);
            previous.RemoveAll();
        }

        session.Clear();
        var routing = new RoutingCache(applier, liveDestinations, geoSplit, geo?.Routes ?? [], listDirect, blockRoutes, appSettings.RouteTtlSeconds, loggerFactory.CreateLogger<RoutingCache>(), tunnelResolver);
        session.SetCache(routing);
        // The agent answers the UI from its own process, where these caches do not exist, and a rule change is
        // announced the same way instead of being polled for.
        _ = Task.Run(() => RuntimeSnapshotPipe.ServeAsync(name, (op, ct) => ServeAsync(op, routing, name, ct), logger, sessionCts.Token));
        _ = Task.Run(() => routing.RunAsync(sessionCts.Token));
        _ = Task.Run(() => routing.PumpAsync(sessionCts.Token));
        var ranges = routing.RangeCounts;
        logger.LogInformation("{Name}: routing rules ready — {Proxy} range(s) go through the tunnel, {Direct} stay outside it, {Block} are refused; each address is decided when it is first used and forgotten after {Ttl} s unused",
            name, ranges.Proxy, ranges.Direct, ranges.Block, routing.TtlSeconds);

        // Tracker when there's live work or a routing list drives the split.
        DomainTracker? tracker = null;
        if (trackDomains || trackApps || allUdp || (geoSplit && activeRoutingListId is not null))
        {
            var peer = WgConfigEditor.GetPeerPublicKey(config);
            if (peer is not null)
            {
                // Started after the geo-domain sink is attached to avoid a rebuild race.
                // With lazy ranges the tracker owns only what it installs: the advertised set at bring-up is the
                // resolver infrastructure, and the list's own ranges are decided per destination by the cache.
                var trackerStatic = geoSplit ? startupRoutes : geoRoutes;
                var trackerList = geoSplit ? new List<string>() : listRoutes;
                tracker = new DomainTracker(store, routes, uapi, loggerFactory.CreateLogger<DomainTracker>(), name, peer, trackerStatic, trackerList, appSettings.RouteTtlSeconds, stripV6, geoSplit, routing, synReset);
                session.SetTracker(tracker);
                routing.SetAdoptionCheck(tracker.Holds);
            }
        }

        // App matcher, built before the proxy so per-app DNS can consult it: resolves whether a PID belongs to
        // the app rules. The DNS-Client tracker marks names queried by matched apps for tunnel resolution.
        AppMatcher? matcher = null;
        AppDnsTracker? appDns = null;
        if (trackApps && tracker is not null)
        {
            var candidate = new AppMatcher(apps, loggerFactory.CreateLogger<AppMatcher>());
            if (candidate.HasMatchers)
            {
                matcher = candidate;
                appDns = new AppDnsTracker(matcher, loggerFactory.CreateLogger<AppDnsTracker>());
                // A matched app's destination is dropped before anything observes it, so the drop itself is where
                // the app rule has to be applied: its remotes take the tunnel instead of a permit.
                routing.SetAppMatch(matcher.MatchesDevicePath);
                // The same rule over the connection table: a half-open attempt is seen there whether or not the
                // firewall reported its drop, and one already permitted outside is moved back onto the tunnel.
                liveDestinations.SetAppMatch(matcher.MatchPids);
            }
        }

        // An app that reaches bare addresses has no name to resolve, so each of them is learned by watching the app
        // fail once. Remembering them turns that into a one-off: they are routed at the next bring-up, and the
        // moment one of them reappears the rest go with it.
        if (matcher is not null && tracker is not null)
        {
            var appMemory = new AppDestinationMemory(store, tracker, name, apps, loggerFactory.CreateLogger<AppDestinationMemory>());
            routing.AppDestination += appMemory.Note;
            tracker.SetAppDestinationSink(appMemory.Note);
            tracker.SetAppMemoryCheck(appMemory.Holds);
            _ = Task.Run(() => appMemory.RunAsync(sessionCts.Token));
        }

        var proxy = StartProxy(trackDomains ? domains : [], blockDomains, stripV6, geoSplit, tunnelResolver, localResolver, lanResolvers, exclusionDomains, directDomains, tracker, appDns, routing);
        session.SetProxy(proxy);

        // The clients of the access point go out under the rules of this machine: what they open is terminated
        // on an adapter of ours and opened again here, so the routing table decides it and the sharing NAT does
        // not. The gateway follows the point up and down, and goes away with this session.
        if (proxy is not null)
        {
            var gateway = new HotspotGateway(proxy, routes, new DirectProxyOutbound(), effectiveMtu, routing.Note, loggerFactory.CreateLogger<HotspotGateway>());
            sessionCts.Token.Register(gateway.Dispose);
            _ = Task.Run(() => gateway.RunAsync(sessionCts.Token));
        }

        // Per-app DNS: a name queried by a matched app resolves through the tunnel and routes its answer. On
        // learn, drop the proxy's pre-mark answer AND flush the OS resolver cache so the app's retry re-queries
        // through the proxy instead of Dnscache's cached pre-mark result (mirrors the geo live-add flush). The
        // flush is coalesced because a chatty app learns many names.
        if (proxy is not null && appDns is not null)
        {
            // A promoted domain takes the same path as an ETW-learned name: apps with their own resolver (Chromium)
            // never reach DNS-Client, so their domains are learned from the traffic they generate.
            if (tracker is not null)
            {
                tracker.DomainPromoted += appDns.MarkName;
            }

            var learnFlush = new SemaphoreSlim(0, 1);
            appDns.NameLearned += learnedName =>
            {
                proxy.InvalidateName(learnedName);
                try
                {
                    learnFlush.Release();
                }
                catch (SemaphoreFullException)
                {
                }
            };
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!sessionCts.Token.IsCancellationRequested)
                    {
                        await learnFlush.WaitAsync(sessionCts.Token);
                        // Collapse a burst of newly-learned names into one flush.
                        await Task.Delay(500, sessionCts.Token);
                        while (learnFlush.Wait(0))
                        {
                        }

                        dns.FlushCache();
                    }
                }
                catch (OperationCanceledException)
                {
                }
            });
            _ = Task.Run(() => appDns.RunAsync(sessionCts.Token));
        }

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
            logger.LogWarning("port 53 on this machine is taken by another program, so names cannot be handled here; the resolvers are used directly and rules by domain name will not apply — only rules by address will");
        }

        logger.LogDebug("{Name}: name handling {State}, domain tracking {Tracker} [{Elapsed} ms in]",
            name, proxy?.BoundV4 is not null ? $"listening on {proxy.BoundV4}" : "unavailable", tracker is not null ? "on" : "off", connectSw.ElapsedMilliseconds);

        // Strip DNS from config; we apply it on the adapter ourselves.
        config = WgConfigEditor.RemoveDns(config);

        // Defer the DNS redirect until the peer first answers. The proxy is already bound, but until the tunnel
        // is up it can only answer tunnel-routed names once the handshake lands; redirecting up front strands the
        // box's resolver for the whole dial when the endpoint never answers (a censored or unreachable server).
        // Mirror the kill-switch's deferral so a dial that never handshakes leaves the OS resolver working.
        var applied = false;
        var dnsApplyTask = Task.CompletedTask;
        if (redirectServers.Count > 0)
        {
            applied = true;
            dnsApplyTask = Task.Run(() => ApplyDnsWhenTunnelUpAsync(name, redirectServers, proxy?.BoundV4, sessionCts.Token));
        }

        if (stripV6 && redirectServers.Count > 0)
        {
            // Set a v4 resolver on the v4-only adapter.
            _ = Task.Run(() => ConfigureTunnelAdapterDnsAsync(name, redirectServers));
        }

        // Exclude wstunnel's real server IP, not the loopback endpoint.
        var endpoint = underlayProbe;
        var excluded = endpoint is not null && routes.AddEndpointExclusion(name, endpoint);

        // Keep the LAN and any manual exclusions direct in both modes (RFC1918 floor + stored list). In split
        // mode the tunnelled geo routes are more specific, so longest-prefix-match keeps them on the tunnel.
        var lanExcluded = routes.AddLanExclusions(name, dualStack: !stripV6, bypassCidrs);
        logger.LogDebug("{Name}: routes in place — the server {Endpoint} is kept outside the tunnel: {Excluded}, your own network too: {Lan} [{Elapsed} ms in]",
            name, endpoint?.ToString() ?? "none", excluded, lanExcluded, connectSw.ElapsedMilliseconds);

        // Whitelist wstunnel under the kill-switch.
        var underlayAppPath = useWebSocket ? TunnelPaths.WsTunnelExe() : null;
        _ = Task.Run(() => ArmFirewallAsync(name, killSwitch, !stripV6, underlayAppPath, bypassCidrs, endpoint, routing, sessionCts.Token));

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

        // Flow tracker: routes matched apps' TCP+UDP remotes by ETW signaling (not DNS); not a using to avoid racing
        // Task.Run. It also feeds the routing cache, which needs no app matcher - that is how an address reached
        // without a DNS lookup earns its Direct route.
        if ((tracker is not null && (matcher is not null || allUdp)) || routing is not null)
        {
            var flowTracker = new NetworkFlowTracker(matcher, tracker, allUdp, !stripV6, endpoint, loggerFactory.CreateLogger<NetworkFlowTracker>(), routing is null ? null : routing.Note);
            // A released destination must lose its dedupe record too, or the next packet to it is skipped and the
            // route never comes back.
            tracker?.SetForgetSink(flowTracker.Forget);
            _ = Task.Run(() => flowTracker.RunAsync(sessionCts.Token));
        }

        // Start wstunnel last so a failure can't orphan it.
        if (useWebSocket)
        {
            wsTransport = await WsTunnelTransport.StartAsync(wsHost!, wsPort, wsTargetPort, wsPathPrefix, wsCredentials,
                line => RecordRejection(name, line), loggerFactory.CreateLogger<WsTunnelTransport>(), CancellationToken.None);
            if (wsTransport is null)
            {
                throw new ConnectFailureException(ConnectFailureReason.UnderlayUnreachable, $"WebSocket transport (wstunnel) failed to start for {name}");
            }

            config = WgConfigEditor.SetEndpoint(config, $"127.0.0.1:{wsTransport.LocalPort}");
            logger.LogInformation("{Name}: the websocket carrier is up and the tunnel now dials it locally on port {Port}, so to the provider this looks like ordinary web traffic", name, wsTransport.LocalPort);
        }

        config = WgConfigEditor.SetMtu(config, effectiveMtu);
        // Keep the peer handshake/NAT state warm so a lossy underlay doesn't let the session age out into a
        // forced re-dial; only injected when the imported config didn't already specify its own keepalive.
        config = WgConfigEditor.EnsurePersistentKeepalive(config, DefaultKeepaliveSeconds);
        logger.LogInformation("{Name}: packet size set to {Mtu} and a keepalive every {Keepalive}s, so a quiet link is not dropped by the provider", name, effectiveMtu, DefaultKeepaliveSeconds);

        // A carrier that stops carrying leaves the session standing, so the link is measured from inside the tunnel
        // and the carrier re-dialled on what the echoes say - the tunnel, its routes and its DNS all stay up.
        if (wsTransport is not null)
        {
            // The config's own DNS is gone from it by now, so the resolvers come from what was read out of it
            // earlier: those are the addresses the tunnel is given routes to, and an echo needs one of them.
            var probe = new LinkLossProbe(LinkLossProbe.Targets(WgConfigEditor.GetAddresses(config), tunnelResolver));
            var watchdog = new CarrierWatchdog(wsTransport, uapi, probe, name, loggerFactory.CreateLogger<CarrierWatchdog>());
            _ = Task.Run(() => probe.RunAsync(sessionCts.Token));
            _ = Task.Run(() => watchdog.RunAsync(sessionCts.Token));
        }

        logger.LogInformation("{Name}: everything is prepared in {Elapsed} ms, starting the tunnel", name, connectSw.ElapsedMilliseconds);

        try
        {
            WireGuardEngine.RunTunnelService(config, TunnelDevice.NameOf(name));
        }
        catch (Exception ex) when (ex is not ConnectFailureException)
        {
            throw new ConnectFailureException(ConnectFailureReason.AdapterStartFailed, ex.Message, ex);
        }
        finally
        {
            logger.LogInformation("{Name}: the session ended after {Elapsed} ms; removing its routes, firewall rules and DNS changes", name, connectSw.ElapsedMilliseconds);
            // Cancel before disabling: arming re-checks the token after Enable, so a late arm undoes itself.
            sessionCts.Cancel();
            session.Clear();
            // Before the engine closes, so the host routes go away with their permits still known.
            routing?.RemoveAll();
            // The batched withdrawals leave now: the device is about to go, and a queued one would never be sent.
            uapi.FlushWithdrawals();
            firewall.Disable();

            if (wsTransport is not null)
            {
                await wsTransport.DisposeAsync();
            }

            if (applied)
            {
                // Let a deferred apply that is mid-flight finish (or observe the cancel) before reverting, so it
                // cannot re-point DNS after the restore. Restore is a no-op when the handshake never landed.
                try
                {
                    await dnsApplyTask;
                }
                catch (OperationCanceledException)
                {
                }

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

    // Records a permanent carrier rejection: the session is already up, so the agent reads the cause from the
    // store instead of inferring a retryable no-handshake.
    private void RecordRejection(string name, string line)
    {
        try
        {
            store.SetSettingAsync(TunnelPaths.ConnectMessageKey(name), line).GetAwaiter().GetResult();
            store.SetSettingAsync(TunnelPaths.ConnectReasonKey(name), nameof(ConnectFailureReason.TransportRejected)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "{Name}: the carrier's rejection could not be saved, so the next attempt may be reported as an unreachable server instead of a refusal", name);
        }
    }

    private DnsProxy? StartProxy(IReadOnlyList<GeoDomain> domains, IReadOnlyList<GeoDomain> blockDomains, bool stripV6, bool localIsLan, IReadOnlyList<string> tunnelUpstream, IReadOnlyList<string> localUpstream, IReadOnlyList<string> lanUpstream, IReadOnlyList<string> localDomains, IReadOnlyList<GeoDomain> directDomains, DomainTracker? tracker, AppDnsTracker? appDns, RoutingCache? routing)
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
        var proxy = new DnsProxy(domains, blockDomains, tunnelIp, localIp, lanIp, lanPool, localIsLan, localDomains, directDomains, tracker, loggerFactory.CreateLogger<DnsProxy>(), stripV6, tunnelSecondary, appDns, routing);
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
            logger.LogInformation("the server name {Host} did not resolve now, so the address {Ip} that worked last time is used", host, cached);
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

    internal static IReadOnlyList<string> SplitDefaultRoutes(IReadOnlyList<string> allowedIps)
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
            if (routes.FindTunnelIndex(name) is { } index)
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
                if (routes.FindTunnelIndex(name) is not null)
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

    // How long the proxy has to answer its own health query, and how often the answer is asked for again.
    private const int DnsProbeTimeoutMs = 2000;
    private static readonly TimeSpan DnsProbeRetryDelay = TimeSpan.FromSeconds(15);

    // Applies the host DNS redirect once the peer first answers, then flushes so pre-redirect answers are
    // re-queried through the proxy. Before the handshake the OS keeps its own resolvers, so a dial that never
    // completes cannot strand the machine's DNS; the teardown reverts whatever this applied.
    private async Task ApplyDnsWhenTunnelUpAsync(string name, IReadOnlyList<string> redirectServers, IPAddress? proxyAddress, CancellationToken ct)
    {
        try
        {
            await WaitForHandshakeAsync(name, ct);

            // Ask the proxy before handing it every lookup on this machine: pointing the adapters at a resolver
            // that answers nothing leaves the machine with no DNS at all. Keep asking, so a proxy that starts
            // answering later still gets the redirect.
            var attempt = 0;
            while (proxyAddress is not null && !await DnsHealthProbe.AnswersAsync(proxyAddress, DnsProbeTimeoutMs, ct).ConfigureAwait(false))
            {
                attempt++;
                if (attempt == 1)
                {
                    logger.LogWarning("{Name}: the name proxy on {Address} does not answer its own query, so the adapters keep their own resolvers; rules by domain do not apply, only rules by address", name, proxyAddress);
                }
                else
                {
                    logger.LogDebug("{Name}: the name proxy on {Address} still answers nothing (attempt {Attempt})", name, proxyAddress, attempt);
                }

                await Task.Delay(DnsProbeRetryDelay, ct).ConfigureAwait(false);
            }

            using (logger.Step("apply DNS + flush cache"))
            {
                dns.Apply(name, redirectServers);
                dns.FlushCache();
            }

            logger.LogDebug("{Name}: the server answered, so name lookups now go to {Servers} and the cached ones were cleared", name, string.Join(",", redirectServers));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private const int FirewallArmAttempts = 4;
    private static readonly TimeSpan FirewallArmRetryDelay = TimeSpan.FromSeconds(2);


    // Re-decides the routing cache's verdicts after a live list edit. Separate from the domain tracker: that one
    // only runs in split mode, while the cache owns the Direct and Block buckets in both. The current generation
    // is seeded first so the freshly built rule set is not dropped and reinstalled on the first tick.
    // Answers the agent: the cache snapshot it renders, or a rule change it just persisted.
    private async Task<string> ServeAsync(string op, RoutingCache routing, string tunnelName, CancellationToken ct)
    {
        if (op == RuntimeSnapshotPipe.OpRules)
        {
            await ApplyRulesAsync(routing, tunnelName, ct);
            return "ok";
        }

        if (op == RuntimeSnapshotPipe.OpTtl)
        {
            await ApplyTtlAsync(routing, ct);
            return "ok";
        }

        if (op == RuntimeSnapshotPipe.OpCounts)
        {
            return System.Text.Json.JsonSerializer.Serialize(inspector.Counts());
        }

        if (op == RuntimeSnapshotPipe.OpSessions)
        {
            return inspector.Sessions().ToPayload();
        }

        if (op.StartsWith(RuntimeSnapshotPipe.OpProbe, StringComparison.Ordinal))
        {
            var asked = op.Split('\t');
            var report = await ProbeRoute.RunAsync(
                routing,
                asked.Length > 1 ? asked[1] : string.Empty,
                asked.Length > 2 && asked[2].Length > 0 ? asked[2] : ProbePaths.Auto,
                asked.Length > 3 ? asked[3] : string.Empty,
                ct);
            logger.LogInformation("probe: {Header}", report.Render().Split('\n')[0].Trim());
            return report.ToPayload();
        }

        return System.Text.Json.JsonSerializer.Serialize(inspector.Collect());
    }

    // Re-reads the stored lifetime and hands it to what already holds routes. The store is the one both processes
    // share, so the agent persists the value and this only adopts it.
    private async Task ApplyTtlAsync(RoutingCache routing, CancellationToken ct)
    {
        try
        {
            var current = await settings.LoadAsync(ct);
            routing.SetTtl(current.RouteTtlSeconds);
            session.Tracker?.SetTtl(current.RouteTtlSeconds);
            logger.LogInformation("an address unused for {Ttl} s is now forgotten and decided again on the next contact", current.RouteTtlSeconds);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "the new address lifetime could not be applied; the previous one stays in force until reconnect");
        }
    }

    // Re-reads the active list and decides every destination in use against it; one that changed side moves at once.
    private async Task ApplyRulesAsync(RoutingCache routing, string tunnelName, CancellationToken ct)
    {
        try
        {
            var current = await store.GetActiveRoutingListMaterializationAsync(tunnelName, ct);
            if (current is null)
            {
                return;
            }

            var list = await store.GetRoutingListAsync(current.ListId, ct);
            if (list is not null)
            {
                // Read before the rebuild: these are the destinations a rule by name has to be applied to.
                var held = routing.Snapshot();
                routing.Rebuild(current.Routes, list.DirectRoutes, list.BlockRoutes);

                // The Direct and Block names live in the proxy, outside the tracker: it only runs in split mode,
                // while these two buckets decide in both.
                if (session.Proxy is { } proxy && proxy.UpdateBuckets(list.BlockDomains, list.DirectDomains))
                {
                    dns.FlushCache();
                }

                ApplyNameRules(routing, held);
            }

            session.Tracker?.ApplyList(current, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "{Tunnel}: the edited rules could not be applied to the running tunnel; it keeps working by the previous ones until reconnect", tunnelName);
        }
    }

    // Applies the buckets by name to the destinations already in use. A rule by name reaches an address only
    // through a lookup, and an address that is already carrying traffic is never looked up again: its client holds
    // the answer. So the names each held address was resolved from are read here and their verdict is applied
    // straight to it, which is what makes an edit take effect on a running download.
    private void ApplyNameRules(RoutingCache routing, IReadOnlyList<RoutingCache.Held> held)
    {
        if (session.Proxy is not { } proxy || session.Tracker is not { } tracker)
        {
            return;
        }

        var applied = 0;
        foreach (var entry in held)
        {
            // An adopted address belongs to a tracked name, and the tracker applies the list to it itself.
            if (entry.Adopted)
            {
                continue;
            }

            var verdict = RouteVerdict.None;
            foreach (var domain in tracker.NamesOf(entry.Address.ToString()))
            {
                var byName = proxy.NameVerdict(domain);
                if (byName == RouteVerdict.Block)
                {
                    verdict = byName;
                    break;
                }

                if (byName == RouteVerdict.Direct)
                {
                    verdict = byName;
                }
            }

            if (verdict is not (RouteVerdict.Direct or RouteVerdict.Block))
            {
                continue;
            }

            routing.Note(entry.Address, verdict);
            applied++;
        }

        if (applied > 0)
        {
            logger.LogInformation("{Count} address(es) in use are decided by a rule by name and were moved onto the path it asks for, without waiting for their traffic to stop", applied);
        }
    }

    private async Task ArmFirewallAsync(string name, bool killSwitch, bool dualStack, string? underlayAppPath, IReadOnlyList<string> extraLanCidrs, IPAddress? endpoint, RoutingCache? routing, CancellationToken ct)
    {
        try
        {
            var index = await WaitForAdapterAsync(name, ct);
            if (index is null)
            {
                logger.LogWarning("{Name}: the tunnel adapter never appeared, so the leak protection could not be set up; if the tunnel does come up, traffic may leave past it", name);
                return;
            }

            if (killSwitch)
            {
                // The kill-switch protects an established tunnel, not the dial: a server that never answers
                // would otherwise firewall the machine off for the whole attempt (#208).
                logger.LogDebug("{Name}: the leak protection waits for the server's first answer, so a server that never replies cannot cut this machine off the network", name);
                await WaitForHandshakeAsync(name, ct);
            }

            // Soft block only where a verdict is still coming: without the cache nothing would ever unblock the retry.
            if (await ArmWithRetryAsync(() => Arm(index.Value, killSwitch, dualStack, underlayAppPath, extraLanCidrs, routing is not null, endpoint, ct), ct))
            {
                // Arming rebuilds the filter set, so host permits from the previous generation are gone with it.
                routing?.Reinstall();

                // A destination dropped before its verdict exists is announced by the drop itself; nothing else
                // sees it, because the send that would raise a connect or datagram event never happens.
                if (routing is not null && !firewall.WatchDrops(routing.Report))
                {
                    logger.LogWarning("{Name}: the firewall does not report what it blocks on this system, so an address reached without a name lookup — an app with a hard-coded address — stays blocked instead of being routed", name);
                }
            }
            else
            {
                logger.LogError("{Name}: the leak protection did not take after {Attempts} attempts; the tunnel is running unprotected, so if it drops, traffic goes out in the clear — reconnect to restore it", name, FirewallArmAttempts);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "{Name}: setting up the leak protection failed; the tunnel runs without it until the next reconnect", name);
        }
    }

    // Installs the filters and returns whether they survived. The session cancels before the teardown disables,
    // so a set that lands after it undoes itself here.
    private bool Arm(uint index, bool killSwitch, bool dualStack, string? underlayAppPath, IReadOnlyList<string> extraLanCidrs, bool softBlock, IPAddress? endpoint, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var armed = firewall.Enable(index, killSwitch, dualStack, underlayAppPath, extraLanCidrs, softBlock, endpoint);
        if (ct.IsCancellationRequested)
        {
            firewall.Disable();
            return false;
        }

        return armed;
    }

    // Retries the arm: a sublayer left by an overlapping teardown clears within seconds; without a retry the tunnel
    // would run unprotected (or behind a stale block-all) until the next reconnect.
    private async Task<bool> ArmWithRetryAsync(Func<bool> arm, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            if (arm())
            {
                return true;
            }

            if (attempt >= FirewallArmAttempts || ct.IsCancellationRequested)
            {
                return false;
            }

            logger.LogWarning("the leak protection did not take on attempt {Attempt}, usually because a previous session is still letting go of it; retrying in {Delay}s", attempt, FirewallArmRetryDelay.TotalSeconds);
            await Task.Delay(FirewallArmRetryDelay, ct);
        }
    }

    // Returns the tunnel interface index, or null when the adapter never appears.
    private async Task<uint?> WaitForAdapterAsync(string name, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (routes.FindTunnelIndex(name) is { } index)
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
