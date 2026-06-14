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
    ILoggerFactory loggerFactory,
    ILogger<TunnelRunner> logger)
{
    /// <summary>
    /// Loads the tunnel config and materialized geo set, then hands control to the native service loop.
    /// </summary>
    public async Task RunAsync(string name)
    {
        dns.RestoreSaved();

        var config = await File.ReadAllTextAsync(TunnelPaths.ConfigFile(name));
        var geo = await store.GetTunnelGeoAsync(name);

        var geoSplit = geo?.GeoSplit ?? false;
        var geoRoutes = geo?.Routes ?? [];
        var domains = geo?.Domains ?? [];

        var allowedIps = AllowedIpsResolver.Build(geoSplit, WgConfigEditor.GetAllowedIps(config), geoRoutes);
        config = WgConfigEditor.ApplyAllowedIps(config, allowedIps);

        var appSettings = await settings.LoadAsync();
        var applied = false;
        if (geoSplit && domains.Count > 0 && StartGeo(name, config, domains, geoRoutes, appSettings.RefreshSeconds))
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

    private bool StartGeo(string name, string config, IReadOnlyList<GeoDomain> domains, IReadOnlyList<string> geoRoutes, int refreshSeconds)
    {
        var peer = WgConfigEditor.GetPeerPublicKey(config);
        if (peer is null)
        {
            return false;
        }

        var tracker = new DomainTracker(store, routes, uapi, loggerFactory.CreateLogger<DomainTracker>(), name, peer, geoRoutes, refreshSeconds);
        _ = Task.Run(tracker.RunAsync);

        try
        {
            var proxy = new DnsProxy(domains, IPAddress.Parse("1.1.1.1"), tracker, loggerFactory.CreateLogger<DnsProxy>());
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
}
