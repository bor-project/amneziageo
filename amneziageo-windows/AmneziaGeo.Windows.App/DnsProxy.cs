using System.Collections.Concurrent;
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
/// Caches answers for their TTL and, on an IPv4-only tunnel, denies AAAA so clients never stall
/// attempting dead IPv6 addresses.
/// </summary>
internal sealed class DnsProxy
{
    private const int UpstreamTimeoutMs = 5000;
    private const int SioUdpConnReset = unchecked((int)0x9800000C);
    private const int TypeAaaa = 28;
    private const int MinCacheSeconds = 10;
    private const int MaxCacheSeconds = 300;

    private readonly List<UdpClient> _servers = [];
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<GeoDomain> _domains;
    private readonly IPAddress _upstream;
    private readonly DomainTracker _tracker;
    private readonly ILogger<DnsProxy> _logger;
    private readonly bool _stripV6;

    /// <summary>
    /// ctor
    /// </summary>
    public DnsProxy(IReadOnlyList<GeoDomain> domains, IPAddress upstream, DomainTracker tracker, ILogger<DnsProxy> logger, bool stripV6)
    {
        _domains = domains;
        _upstream = upstream;
        _tracker = tracker;
        _logger = logger;
        _stripV6 = stripV6;
        Bind(IPAddress.Loopback);
        Bind(IPAddress.IPv6Loopback);
        if (_servers.Count == 0)
        {
            throw new InvalidOperationException("dns proxy could not bind any loopback address on :53");
        }
    }

    private sealed record CacheEntry(byte[] Response, DateTime Expiry);

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
            var name = DnsMessage.QuestionName(query);
            var type = DnsMessage.QuestionType(query);

            byte[] response;
            if (_stripV6 && type == TypeAaaa)
            {
                // IPv4-only tunnel: return NODATA for AAAA so clients use IPv4 instead of stalling
                // on IPv6 addresses that route into a tunnel with no IPv6 transit.
                response = DnsMessage.BuildNoData(query);
            }
            else if (TryGetCached(name, type, query, out var cached))
            {
                response = cached;
            }
            else
            {
                response = Forward(query);
                StoreInCache(name, type, response);
            }

            lock (server)
            {
                server.Send(response, response.Length, client);
            }

            TrackIfMatched(name, response);
        }
        catch (Exception)
        {
        }
    }

    private bool TryGetCached(string? name, int type, byte[] query, out byte[] response)
    {
        response = [];
        if (name is null)
        {
            return false;
        }

        if (_cache.TryGetValue(CacheKey(name, type), out var entry) && entry.Expiry > DateTime.UtcNow)
        {
            response = (byte[])entry.Response.Clone();
            if (response.Length >= 2 && query.Length >= 2)
            {
                response[0] = query[0];
                response[1] = query[1]; // match the caller's transaction id
            }

            return true;
        }

        return false;
    }

    private void StoreInCache(string? name, int type, byte[] response)
    {
        if (name is null)
        {
            return;
        }

        var ttl = DnsMessage.MinTtl(response);
        if (ttl <= 0)
        {
            return; // nothing cacheable (no answers, or an error response)
        }

        var seconds = Math.Clamp(ttl, MinCacheSeconds, MaxCacheSeconds);
        _cache[CacheKey(name, type)] = new CacheEntry((byte[])response.Clone(), DateTime.UtcNow.AddSeconds(seconds));
    }

    private static string CacheKey(string name, int type)
    {
        return type.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + name.TrimEnd('.').ToLowerInvariant();
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

    private void TrackIfMatched(string? name, byte[] response)
    {
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
