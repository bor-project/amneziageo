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
    DnsRedirector dns,
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

        var allowedIps = AllowedIpsResolver.Build(geoSplit, WgConfigEditor.GetAllowedIps(config), geoRoutes);
        config = WgConfigEditor.ApplyAllowedIps(config, allowedIps);

        var appSettings = await settings.LoadAsync();
        var stripV6 = !HasIpv6Address(config);
        if (stripV6)
        {
            // The native service loop below brings up an IPv4-only tunnel adapter; disable its IPv6 so
            // Windows stops handing it dead fec0:: DNS servers that stall the system resolver. Runs in
            // both split and full-tunnel modes and is reapplied on every (re)connect.
            _ = Task.Run(() => SuppressTunnelIpv6Async(name));
        }

        var applied = false;
        if (geoSplit && domains.Count > 0 && StartGeo(name, config, domains, geoRoutes, appSettings.RefreshSeconds, stripV6))
        {
            dns.Apply(["127.0.0.1"]);
            config = WgConfigEditor.RemoveDns(config);
            applied = true;
        }
        else
        {
            var dnsServers = WgConfigEditor.GetDns(config);
            if (dnsServers.Count > 0)
            {
                dns.Apply(dnsServers);
                config = WgConfigEditor.RemoveDns(config);
                applied = true;
            }
        }

        var endpoint = TunnelEndpoint.Resolve(config);
        var excluded = endpoint is not null && routes.AddEndpointExclusion(endpoint);
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
                routes.RemoveEndpointExclusion(endpoint!);
            }
        }
    }

    private bool StartGeo(string name, string config, IReadOnlyList<GeoDomain> domains, IReadOnlyList<string> geoRoutes, int refreshSeconds, bool stripV6)
    {
        var peer = WgConfigEditor.GetPeerPublicKey(config);
        if (peer is null)
        {
            return false;
        }

        var tracker = new DomainTracker(store, routes, uapi, loggerFactory.CreateLogger<DomainTracker>(), name, peer, geoRoutes, refreshSeconds, stripV6);
        _ = Task.Run(tracker.RunAsync);

        try
        {
            var proxy = new DnsProxy(domains, IPAddress.Parse("1.1.1.1"), tracker, loggerFactory.CreateLogger<DnsProxy>(), stripV6);
            var thread = new Thread(proxy.Serve)
            {
                IsBackground = true,
            };
            thread.Start();
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "failed to start dns proxy");
            return false;
        }
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

    private async Task SuppressTunnelIpv6Async(string name)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (routes.FindInterfaceIndex(name) is not null)
            {
                routes.DisableIpv6(name);
                return;
            }

            await Task.Delay(500);
        }
    }
}
