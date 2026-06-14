using System.Net;
using System.Net.Sockets;
using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Loopback DNS proxy that feeds resolved tunneled domains to the domain tracker.
/// </summary>
internal sealed class DnsProxy
{
    private readonly UdpClient _server;
    private readonly IReadOnlyList<GeoDomain> _domains;
    private readonly IPAddress _upstream;
    private readonly DomainTracker _tracker;
    private readonly ILogger<DnsProxy> _logger;

    /// <summary>
    /// ctor
    /// </summary>
    public DnsProxy(IReadOnlyList<GeoDomain> domains, IPAddress upstream, DomainTracker tracker, ILogger<DnsProxy> logger)
    {
        _domains = domains;
        _upstream = upstream;
        _tracker = tracker;
        _logger = logger;
        _server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 53));
        _server.Client.IOControl(unchecked((int)0x9800000C), new byte[4], null);
    }

    /// <summary>
    /// Serves DNS until the process exits.
    /// </summary>
    public void Serve()
    {
        try
        {
            using (_server)
            {
                while (true)
                {
                    var remote = new IPEndPoint(IPAddress.Any, 0);
                    byte[] query;
                    try
                    {
                        query = _server.Receive(ref remote);
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    byte[] response;
                    try
                    {
                        response = Forward(query);
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    _server.Send(response, response.Length, remote);
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "dns proxy stopped");
        }
    }

    private byte[] Forward(byte[] query)
    {
        using (var client = new UdpClient())
        {
            client.Client.ReceiveTimeout = 5000;
            client.Send(query, query.Length, new IPEndPoint(_upstream, 53));
            var remote = new IPEndPoint(IPAddress.Any, 0);
            return client.Receive(ref remote);
        }
    }

    private void TrackIfMatched(byte[] query, byte[] response)
    {
        var name = DnsMessage.QuestionName(query);
        if (name is null || !DomainMatcher.IsTunneled(name, _domains))
        {
            return;
        }

        var ips = new List<string>();
        foreach (var ip in DnsMessage.Addresses(response))
        {
            ips.Add(ip.ToString());
        }

        if (ips.Count > 0)
        {
            _tracker.Update(name, ips);
        }
    }
}
