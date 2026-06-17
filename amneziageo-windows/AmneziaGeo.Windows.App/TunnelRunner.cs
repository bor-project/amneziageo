using System.Net;
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
    /// <summary>
    /// Loads the tunnel config and materialized geo set, then hands control to the native service loop.
    /// </summary>
    public async Task RunAsync(string name)
    {
        // Start from a clean slate: revert any DNS/route leftovers from a previous tunnel.
        reconciler.Reconcile();

        var config = await File.ReadAllTextAsync(TunnelPaths.ConfigFile(name));
        // Prefer the live balancer routing projection; fall back to the config's own set-geo split.
        var geo = await store.GetActiveTunnelGeoAsync(name);

        var geoSplit = geo?.GeoSplit ?? false;
        var geoRoutes = new List<string>(geo?.Routes ?? []);
        var domains = geo?.Domains ?? [];

        // This tunnel adapter is IPv4-only when the config declares no IPv6 Address.
        var stripV6 = !HasIpv6Address(config);

        // The proxy tracks matched domains only in split mode (in full tunnel everything is already
        // routed; tracking would replace 0.0.0.0/0 with a few /32s).
        var trackDomains = geoSplit && domains.Count > 0;

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

        // Capture the resolvers the system uses now, before redirecting, so the proxy can forward
        // NON-tunneled queries to them — keeps corporate/existing name resolution working alongside
        // another VPN. The loopback proxy always runs (split AND full): on this IPv4-only tunnel it
        // denies AAAA and HTTPS/SVCB (type 65) so dual-stack clients don't stall and Chrome doesn't take
        // an HTTP/3 hint path that bypasses the tunnel.
        var upstream = dns.CaptureUpstream();
        IReadOnlyList<string> redirectServers = [];

        // Local resolver for NON-tunneled names: in split the captured system resolver (coexisting /
        // corporate names keep resolving); in full tunnel everything is tunneled, so resolve all names
        // via the clean resolver above.
        var localResolver = geoSplit
            ? (upstream.Count > 0 ? upstream : tunnelResolver)
            : tunnelResolver;

        var proxy = StartProxy(name, config, trackDomains ? domains : [], geoRoutes, appSettings.RefreshSeconds, stripV6, tunnelResolver, localResolver, trackDomains);
        if (proxy?.BoundV4 is not null)
        {
            redirectServers = [proxy.BoundV4.ToString()];
        }
        else
        {
            // Could not bind loopback :53 (another resolver holds it): degrade to setting resolvers
            // directly — no AAAA/type-65 deny, but connectivity rather than a hang.
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

        var endpoint = TunnelEndpoint.Resolve(config);
        var excluded = endpoint is not null && routes.AddEndpointExclusion(name, endpoint);

        // The engine no longer arms its own kill-switch (we split the default route above), so when the
        // user opts into the kill-switch we arm our own once the tunnel adapter appears. The session
        // token lets teardown cancel a still-pending arm and guarantees the filters come down with the
        // tunnel.
        using var sessionCts = new CancellationTokenSource();

        // The kill-switch blocks everything that does not egress the tunnel interface — a FULL-tunnel
        // concept. In split / routing mode the whole point is that non-routed traffic goes direct, so
        // arming it there severs the entire direct internet and leaves only the handful of tunnel-routed
        // domains reachable (the browser shows ERR_NETWORK_ACCESS_DENIED for everything else). Arm it
        // only in full tunnel; in split mode the routing list — not a blanket firewall — decides what
        // goes through the tunnel, and direct traffic must keep flowing.
        if (appSettings.KillSwitchEnabled && !geoSplit)
        {
            _ = Task.Run(() => ArmKillSwitchAsync(name, appSettings.AllowLan, sessionCts.Token));
        }

        try
        {
            WireGuardEngine.RunTunnelService(config, name);
        }
        finally
        {
            sessionCts.Cancel();
            firewall.Disable();

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
        }
    }

    private DnsProxy? StartProxy(string name, string config, IReadOnlyList<GeoDomain> domains, IReadOnlyList<string> geoRoutes, int refreshSeconds, bool stripV6, IReadOnlyList<string> tunnelUpstream, IReadOnlyList<string> localUpstream, bool trackDomains)
    {
        DomainTracker? tracker = null;
        if (trackDomains)
        {
            var peer = WgConfigEditor.GetPeerPublicKey(config);
            if (peer is null)
            {
                return null;
            }

            tracker = new DomainTracker(store, routes, uapi, loggerFactory.CreateLogger<DomainTracker>(), name, peer, geoRoutes, refreshSeconds, stripV6);
            _ = Task.Run(tracker.RunAsync);
        }

        var tunnelIp = ParseFirst(tunnelUpstream, IPAddress.Parse("1.1.1.1"));
        var localIp = ParseFirst(localUpstream, tunnelIp);
        var proxy = new DnsProxy(domains, tunnelIp, localIp, tracker, loggerFactory.CreateLogger<DnsProxy>(), stripV6);
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
    /// same address space for routing but are not /0 — so the engine routes the full tunnel without
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
    /// Waits for the tunnel adapter to appear, then arms the WFP kill-switch on it. If the tunnel is
    /// torn down before or while arming (the session token is cancelled), the kill-switch is not left
    /// armed: the post-arm re-check disables it, and teardown's own Disable() is idempotent.
    /// </summary>
    private async Task ArmKillSwitchAsync(string name, bool allowLan, CancellationToken ct)
    {
        try
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
            while (DateTimeOffset.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                if (routes.FindInterfaceIndex(name) is { } index)
                {
                    firewall.Enable(index, allowLan);
                    if (ct.IsCancellationRequested)
                    {
                        firewall.Disable();
                    }

                    return;
                }

                await Task.Delay(500, ct);
            }

            logger.LogWarning("kill-switch: tunnel adapter {Name} did not appear; not armed", name);
        }
        catch (OperationCanceledException)
        {
            // Tunnel went down before the adapter came up; nothing to arm.
        }
    }
}
