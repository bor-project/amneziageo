using System.Net;
using System.Net.Sockets;
using AmneziaGeo.Decl;
using AmneziaGeo.Geo;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Loopback DNS proxy that feeds resolved tunneled domains to the domain tracker.
/// </summary>
internal sealed class DnsProxy(IReadOnlyList<GeoDomain> domains, IPAddress upstream, DomainTracker tracker)
{
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
                            TrackIfMatched(query, response);
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

    private void TrackIfMatched(byte[] query, byte[] response)
    {
        var name = DnsMessage.QuestionName(query);
        if (name is null || !DomainMatcher.IsTunneled(name, domains))
        {
            return;
        }

        var ips = new List<string>();
        foreach (var ip in DnsMessage.ARecords(response))
        {
            ips.Add(ip.ToString());
        }

        if (ips.Count > 0)
        {
            tracker.Update(name, ips);
        }
    }
}
