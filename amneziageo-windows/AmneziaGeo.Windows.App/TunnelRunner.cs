using System.Net;
using AmneziaGeo.Config;
using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using AmneziaGeo.Windows.Engine;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Runs a tunnel inside the Windows service process.
/// </summary>
internal static class TunnelRunner
{
    /// <summary>
    /// Loads the tunnel config and materialized geo set, then hands control to the native service loop.
    /// </summary>
    public static async Task RunAsync(string name)
    {
        var config = await File.ReadAllTextAsync(TunnelPaths.ConfigFile(name));

        var dbPath = TunnelPaths.StateDbFile();
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var store = new SqliteStateStore(dbPath);
        await store.InitializeAsync();
        var geo = await store.GetTunnelGeoAsync(name);

        var geoSplit = geo?.GeoSplit ?? false;
        var routes = geo?.Routes ?? [];
        var domains = geo?.Domains ?? [];

        var allowedIps = AllowedIpsResolver.Build(geoSplit, WgConfigEditor.GetAllowedIps(config), routes);
        config = WgConfigEditor.ApplyAllowedIps(config, allowedIps);

        var appConfig = await new ConfigStore(TunnelPaths.AppConfigFile()).LoadAsync();
        DnsRedirector? redirector = null;
        if (geoSplit && domains.Count > 0 && StartGeo(name, config, domains, routes, appConfig.RefreshSeconds, store))
        {
            redirector = new DnsRedirector();
            redirector.Apply();
            config = WgConfigEditor.RemoveDns(config);
        }

        var endpoint = TunnelEndpoint.Resolve(config);
        var excluded = endpoint is not null && RouteManager.AddEndpointExclusion(endpoint);
        try
        {
            WireGuardEngine.RunTunnelService(config, name);
        }
        finally
        {
            redirector?.Restore();
            if (excluded)
            {
                RouteManager.RemoveEndpointExclusion(endpoint!);
            }
        }
    }

    private static bool StartGeo(string name, string config, IReadOnlyList<GeoDomain> domains, IReadOnlyList<string> routes, int refreshSeconds, IStateStore store)
    {
        var peer = WgConfigEditor.GetPeerPublicKey(config);
        if (peer is null)
        {
            return false;
        }

        var tracker = new DomainTracker(name, peer, routes, refreshSeconds, store);
        _ = Task.Run(tracker.RunAsync);

        try
        {
            var proxy = new DnsProxy(domains, IPAddress.Parse("1.1.1.1"), tracker);
            var thread = new Thread(proxy.Serve)
            {
                IsBackground = true,
            };
            thread.Start();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
