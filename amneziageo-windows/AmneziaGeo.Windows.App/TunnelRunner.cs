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
        var geoRoutes = geo?.Routes ?? [];
        var domains = geo?.Domains ?? [];

        // This tunnel adapter is IPv4-only when the config declares no IPv6 Address.
        var stripV6 = !HasIpv6Address(config);

        var allowedIps = AllowedIpsResolver.Build(geoSplit, WgConfigEditor.GetAllowedIps(config), geoRoutes);
        if (stripV6)
        {
            // Never hand IPv6 routes (e.g. a full-tunnel config's ::/0) to a v4-only tunnel: the engine
            // would route IPv6 into a tunnel with no transit while clients still try IPv6 first.
            allowedIps = [.. allowedIps.Where(a => !a.Contains(':'))];
        }

        config = WgConfigEditor.ApplyAllowedIps(config, allowedIps);

        var appSettings = await settings.LoadAsync();

        // Capture the resolvers the system uses now, before redirecting, so the proxy can forward
        // non-geo queries upstream — keeps corporate/existing name resolution working when running
        // alongside another VPN.
        var upstream = dns.CaptureUpstream();
        var configDns = WgConfigEditor.GetDns(config);
        IReadOnlyList<string> redirectServers = [];

        // Always run the loopback DNS proxy — in split AND in full tunnel. On this IPv4-only tunnel it
        // denies AAAA and HTTPS/SVCB (type 65), so dual-stack clients don't stall on IPv6 with no
        // transit and Chrome doesn't take an HTTP/3 hint path that bypasses the tunnel; it also forwards
        // queries to a clean resolver reached through the tunnel. In split mode it additionally tracks
        // matched domains; in full tunnel it must NOT (that would replace 0.0.0.0/0 with a few /32s).
        var trackDomains = geoSplit && domains.Count > 0;

        // Proxy upstream: in full tunnel forward to the config's own resolver, reached cleanly THROUGH
        // the tunnel; in split mode forward to the captured system resolver so coexisting (corporate)
        // name resolution keeps working alongside another VPN.
        var proxyUpstream = geoSplit
            ? (upstream.Count > 0 ? upstream : configDns)
            : (configDns.Count > 0 ? configDns : upstream);

        var proxy = StartProxy(name, config, trackDomains ? domains : [], geoRoutes, appSettings.RefreshSeconds, stripV6, proxyUpstream, trackDomains);
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
        try
        {
            WireGuardEngine.RunTunnelService(config, name);
        }
        finally
        {
            if (applied)
            {
                dns.Restore();
            }

            if (excluded)
            {
                routes.RemoveEndpointExclusion(name, endpoint!);
            }
        }
    }

    private DnsProxy? StartProxy(string name, string config, IReadOnlyList<GeoDomain> domains, IReadOnlyList<string> geoRoutes, int refreshSeconds, bool stripV6, IReadOnlyList<string> upstream, bool trackDomains)
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

        var upstreamIp = upstream.Count > 0 && IPAddress.TryParse(upstream[0], out var parsed)
            ? parsed
            : IPAddress.Parse("1.1.1.1");
        var proxy = new DnsProxy(domains, upstreamIp, tracker, loggerFactory.CreateLogger<DnsProxy>(), stripV6);
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
}
