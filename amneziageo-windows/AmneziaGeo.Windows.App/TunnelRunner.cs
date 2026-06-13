using System.Net;
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

        if (geoSplit && domains.Count > 0)
        {
            StartGeo(name, config, domains, store);
        }

        var endpoint = TunnelEndpoint.Resolve(config);
        var excluded = endpoint is not null && RouteManager.AddEndpointExclusion(endpoint);
        try
        {
            WireGuardEngine.RunTunnelService(config, name);
        }
        finally
        {
            if (excluded)
            {
                RouteManager.RemoveEndpointExclusion(endpoint!);
            }
        }
    }

    private static void StartGeo(string name, string config, IReadOnlyList<GeoDomain> domains, IStateStore store)
    {
        var peer = WgConfigEditor.GetPeerPublicKey(config);
        if (peer is null)
        {
            return;
        }

        var tracker = new DomainTracker(name, peer, store);
        _ = Task.Run(tracker.RunAsync);

        var proxy = new DnsProxy(domains, IPAddress.Parse("1.1.1.1"), tracker);
        var thread = new Thread(proxy.Serve)
        {
            IsBackground = true,
        };
        thread.Start();
    }
}
