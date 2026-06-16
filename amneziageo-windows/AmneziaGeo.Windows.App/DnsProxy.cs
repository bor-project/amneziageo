using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Loopback DNS proxy that forwards queries through the tunnel and feeds resolved tunneled
/// domains to the domain tracker. Binds an IPv4 loopback (127.0.0.1, or an alternative alias when
/// that is taken) and ::1, and the tunnel adapter's resolver is pointed at whatever it bound, so the
/// system resolver is answered; serves each query on the thread pool so a slow upstream reply never
/// stalls other queries. Caches answers for their TTL and, on an IPv4-only tunnel, denies AAAA so
/// clients never stall attempting dead IPv6 addresses.
/// </summary>
internal sealed class DnsProxy
{
    private const int UpstreamTimeoutMs = 5000;
    private const int SioUdpConnReset = unchecked((int)0x9800000C);
    private const int TypeAaaa = 28;
    private const int TypeHttps = 65; // HTTPS/SVCB
    private const int MinCacheSeconds = 10;
    private const int MaxCacheSeconds = 300;

    // IPv4 loopback candidates tried in order: when another resolver (e.g. a second VPN) already
    // holds 127.0.0.1:53 exclusively, fall back to a dedicated loopback alias so we can still
    // intercept — instead of failing to come up.
    private static readonly IPAddress[] V4Candidates = [IPAddress.Loopback, IPAddress.Parse("127.0.0.2")];

    private readonly List<UdpClient> _servers = [];
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<GeoDomain> _domains;
    private readonly IPAddress _upstream;
    private readonly DomainTracker? _tracker;
    private readonly ILogger<DnsProxy> _logger;
    private readonly bool _stripV6;

    /// <summary>
    /// ctor. Binds a loopback DNS endpoint, falling back to an alternative IPv4 loopback alias when
    /// the primary 127.0.0.1:53 is already taken. Never throws: when nothing can bind, <see
    /// cref="BoundV4"/> stays null and the caller degrades (connect without DNS interception).
    /// </summary>
    public DnsProxy(IReadOnlyList<GeoDomain> domains, IPAddress upstream, DomainTracker? tracker, ILogger<DnsProxy> logger, bool stripV6)
    {
        _domains = domains;
        _upstream = upstream;
        _tracker = tracker;
        _logger = logger;
        _stripV6 = stripV6;

        foreach (var candidate in V4Candidates)
        {
            if (Bind(candidate))
            {
                BoundV4 = candidate;
                break;
            }
        }

        if (Bind(IPAddress.IPv6Loopback))
        {
            BoundV6 = IPAddress.IPv6Loopback;
        }
    }

    /// <summary>The IPv4 loopback address the proxy bound, or null if none was free.</summary>
    public IPAddress? BoundV4 { get; }

    /// <summary>The IPv6 loopback address the proxy bound, or null if it could not bind ::1.</summary>
    public IPAddress? BoundV6 { get; }

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

    private bool Bind(IPAddress address)
    {
        try
        {
            var server = new UdpClient(new IPEndPoint(address, 53));
            server.Client.IOControl(SioUdpConnReset, new byte[4], null);
            _servers.Add(server);
            return true;
        }
        catch (SocketException ex)
        {
            _logger.LogWarning(ex, "dns proxy could not bind {Address}:53", address);
            return false;
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
            else if (type == TypeHttps)
            {
                // HTTPS/SVCB records carry ipv4hint/ipv6hint addresses that we cannot intercept and
                // route into the tunnel. Honoring them lets clients (Chrome) connect straight to those
                // hints over HTTP/3, bypassing geo routing — for a geo-blocked destination that is a
                // failed QUIC attempt followed by a slow fallback. Deny it (NODATA) so clients fall
                // back to A records, which we do track and route.
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

            // Install routing for a matched domain BEFORE answering. Otherwise the client receives the
            // IP and opens a connection before the tunnel route + allowed-ip exist, so its first SYN
            // egresses off-tunnel and (for a blocked destination) is dropped — costing a multi-second
            // TCP retransmit on every freshly resolved domain. A tracking failure must never withhold
            // the answer, so it is isolated.
            try
            {
                TrackIfMatched(name, response);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "tracking matched domain failed");
            }

            lock (server)
            {
                server.Send(response, response.Length, client);
            }
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
            _tracker?.Update(name, ips);
        }
    }
}
