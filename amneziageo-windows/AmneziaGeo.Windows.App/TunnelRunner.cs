using AmneziaGeo.Windows.Engine;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Runs a tunnel inside the Windows service process.
/// </summary>
internal static class TunnelRunner
{
    /// <summary>
    /// Loads the tunnel config and hands control to the native service loop.
    /// </summary>
    public static void Run(string name)
    {
        var config = File.ReadAllText(TunnelPaths.ConfigFile(name));
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
}
