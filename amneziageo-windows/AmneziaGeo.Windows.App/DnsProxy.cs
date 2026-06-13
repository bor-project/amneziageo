using System.Net;
using System.Net.Sockets;
using AmneziaGeo.Decl;
using AmneziaGeo.Geo;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Loopback DNS proxy that routes resolved tunneled domains through the tunnel.
/// </summary>
internal sealed class DnsProxy(string tunnelName, string peerPublicKey, IReadOnlyList<GeoDomain> domains, IPAddress upstream)
{
    private GeoActivator? _activator;

    /// <summary>
    /// Serves DNS on the loopback address until the process exits.
    /// </summary>
    public void Serve()
    {
        try
        {
            using (var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 53)))
            {
                while (true)
                {
                    var remote = new IPEndPoint(IPAddress.Any, 0);
                    var query = server.Receive(ref remote);
                    byte[] response;
                    try
                    {
                        response = Forward(query);
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    server.Send(response, response.Length, remote);
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try
                        {
                            ActivateIfMatched(query, response);
                        }
                        catch (Exception)
                        {
                        }
                    });
                }
            }
        }
        catch (Exception)
        {
        }
    }

    private byte[] Forward(byte[] query)
    {
        using (var client = new UdpClient())
        {
            client.Client.ReceiveTimeout = 5000;
            client.Send(query, query.Length, new IPEndPoint(upstream, 53));
            var remote = new IPEndPoint(IPAddress.Any, 0);
            return client.Receive(ref remote);
        }
    }

    private void ActivateIfMatched(byte[] query, byte[] response)
    {
        var name = DnsMessage.QuestionName(query);
        if (name is null || !DomainMatcher.IsTunneled(name, domains))
        {
            return;
        }

        var activator = ResolveActivator();
        if (activator is null)
        {
            return;
        }

        foreach (var ip in DnsMessage.ARecords(response))
        {
            activator.TunnelIp(ip);
        }
    }

    private GeoActivator? ResolveActivator()
    {
        if (_activator is not null)
        {
            return _activator;
        }

        var index = RouteManager.FindInterfaceIndex(tunnelName);
        if (index is null)
        {
            return null;
        }

        _activator = new GeoActivator(tunnelName, peerPublicKey, index.Value);
        return _activator;
    }
}
