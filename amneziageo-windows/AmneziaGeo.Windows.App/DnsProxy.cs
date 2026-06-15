using System.Net;
using System.Net.Sockets;
using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Loopback DNS proxy that forwards queries through the tunnel and feeds resolved tunneled
/// domains to the domain tracker. Binds both IPv4 (127.0.0.1) and IPv6 (::1) loopback so the
/// system resolver — which is pointed at both by <see cref="DnsRedirector"/> — is always answered,
/// and serves each query on the thread pool so a slow upstream reply never stalls other queries.
/// </summary>
internal sealed class DnsProxy
{
    private const int UpstreamTimeoutMs = 5000;
    private const int SioUdpConnReset = unchecked((int)0x9800000C);

    private readonly List<UdpClient> _servers = [];
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
        Bind(IPAddress.Loopback);
        Bind(IPAddress.IPv6Loopback);
        if (_servers.Count == 0)
        {
            throw new InvalidOperationException("dns proxy could not bind any loopback address on :53");
        }
    }

    /// <summary>
    /// Serves DNS on every bound loopback address until the sockets close (process exit).
    /// </summary>
    public void Serve()
    {
        var threads = new List<Thread>();
        foreach (var server in _servers)
        {
            var thread = new Thread(() => ServeOne(server))
            {
                IsBackground = true,
            };
            thread.Start();
            threads.Add(thread);
        }

        foreach (var thread in threads)
        {
            thread.Join();
        }
    }

    private void Bind(IPAddress address)
    {
        try
        {
            var server = new UdpClient(new IPEndPoint(address, 53));
            server.Client.IOControl(SioUdpConnReset, new byte[4], null);
            _servers.Add(server);
        }
        catch (SocketException ex)
        {
            _logger.LogWarning(ex, "dns proxy could not bind {Address}:53", address);
        }
    }

    private void ServeOne(UdpClient server)
    {
        var anyEndpoint = server.Client.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any;
        try
        {
            using (server)
            {
                while (true)
                {
                    var remote = new IPEndPoint(anyEndpoint, 0);
                    byte[] query;
                    try
                    {
                        query = server.Receive(ref remote);
                    }
                    catch (SocketException)
                    {
                        continue;
                    }

                    var client = remote;
                    ThreadPool.QueueUserWorkItem(_ => Handle(server, query, client));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "dns proxy stopped on {Address}", anyEndpoint);
        }
    }

    private void Handle(UdpClient server, byte[] query, IPEndPoint client)
    {
        try
        {
            var response = Forward(query);
            lock (server)
            {
                server.Send(response, response.Length, client);
            }

            TrackIfMatched(query, response);
        }
        catch (Exception)
        {
        }
    }

    private byte[] Forward(byte[] query)
    {
        using (var client = new UdpClient())
        {
            client.Client.ReceiveTimeout = UpstreamTimeoutMs;
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
