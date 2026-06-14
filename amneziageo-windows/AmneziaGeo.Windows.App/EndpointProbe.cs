using System.Net.NetworkInformation;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Out-of-band reachability check for a member's endpoint, without bringing up its tunnel.
/// </summary>
internal sealed class EndpointProbe
{
    /// <summary>
    /// Returns the ICMP round-trip time to the member's resolved endpoint in milliseconds, or null when unreachable.
    /// </summary>
    public async Task<long?> PingAsync(string member, int timeoutMs)
    {
        var configPath = TunnelPaths.ConfigFile(member);
        if (!File.Exists(configPath))
        {
            return null;
        }

        var endpoint = TunnelEndpoint.Resolve(await File.ReadAllTextAsync(configPath));
        if (endpoint is null)
        {
            return null;
        }

        try
        {
            using (var ping = new Ping())
            {
                var reply = await ping.SendPingAsync(endpoint, timeoutMs);
                return reply.Status == IPStatus.Success ? reply.RoundtripTime : null;
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Returns whether the member's resolved endpoint answers an ICMP echo within the timeout.
    /// </summary>
    public async Task<bool> IsReachableAsync(string member, int timeoutMs)
    {
        return await PingAsync(member, timeoutMs) is not null;
    }
}
