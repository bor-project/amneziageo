using System.Net;
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
    /// Loads the tunnel config, applies geo settings, and hands control to the native service loop.
    /// </summary>
    public static void Run(string name)
    {
        var config = File.ReadAllText(TunnelPaths.ConfigFile(name));
        var settings = GeoStore.Load(TunnelPaths.GeoFile(name));
        var allowedIps = AllowedIpsResolver.Build(settings, WgConfigEditor.GetAllowedIps(config));
        config = WgConfigEditor.ApplyAllowedIps(config, allowedIps);

        if (settings.GeoSplit)
        {
            StartDnsProxy(name, config, settings);
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

    private static void StartDnsProxy(string name, string config, GeoSettings settings)
    {
        var peer = WgConfigEditor.GetPeerPublicKey(config);
        if (peer is null)
        {
            return;
        }

        var proxy = new DnsProxy(name, peer, settings, IPAddress.Parse("1.1.1.1"));
        var thread = new Thread(proxy.Serve)
        {
            IsBackground = true,
        };
        thread.Start();
    }
}
