using System.Net;
using System.Net.Sockets;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Resolves the peer endpoint address from a wg-quick config.
/// </summary>
internal static class TunnelEndpoint
{
    /// <summary>
    /// Returns the resolved IPv4 endpoint address, or null when absent.
    /// </summary>
    public static IPAddress? Resolve(string config)
    {
        foreach (var line in config.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("Endpoint", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = trimmed[(trimmed.IndexOf('=') + 1)..].Trim();
            var colon = value.LastIndexOf(':');
            var host = colon >= 0 ? value[..colon].Trim() : value;
            IPAddress[] addresses;
            try
            {
                addresses = Dns.GetHostAddresses(host);
            }
            catch (SocketException)
            {
                // Engine resolves the endpoint itself.
                return null;
            }

            foreach (var address in addresses)
            {
                if (address.AddressFamily == AddressFamily.InterNetwork)
                {
                    return address;
                }
            }
        }

        return null;
    }
}
