using System.Net.NetworkInformation;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Out-of-band reachability check for a member's endpoint, without bringing up its tunnel.
/// </summary>
internal sealed class EndpointProbe
{
    /// <summary>
    /// Returns whether the member's resolved endpoint answers an ICMP echo within the timeout.
    /// </summary>
    public async Task<bool> IsReachableAsync(string member, int timeoutMs)
    {
        var configPath = TunnelPaths.ConfigFile(member);
        if (!File.Exists(configPath))
        {
            return false;
        }

        var endpoint = TunnelEndpoint.Resolve(await File.ReadAllTextAsync(configPath));
        if (endpoint is null)
        {
            return false;
        }

        try
        {
            using (var ping = new Ping())
            {
                var reply = await ping.SendPingAsync(endpoint, timeoutMs);
                return reply.Status == IPStatus.Success;
            }
        }
        catch (Exception)
        {
            return false;
        }
    }
}
