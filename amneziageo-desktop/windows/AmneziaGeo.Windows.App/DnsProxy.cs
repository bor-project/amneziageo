using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
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
    // intercept - instead of failing to come up.
    private static readonly IPAddress[] V4Candidates = [IPAddress.Loopback, IPAddress.Parse("127.0.0.2")];

    // Built-in suffixes always resolved via the LAN resolver and never tunneled, so the local network
    // keeps working in full tunnel. Single-label names (no dot) and reverse-DNS for private ranges are
    // handled separately in IsLocalName. User entries from the exclusions list are added on top.
    private static readonly string[] BuiltinLocalSuffixes =
        ["local", "lan", "home", "home.arpa", "internal", "intranet", "corp", "localdomain", "localhost"];

    private readonly List<UdpClient> _servers = [];
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<GeoDomain> _domains;
    private readonly IPAddress _tunnelUpstream;
    private readonly IPAddress _localUpstream;
    private readonly IPAddress? _lanUpstream;
    private readonly IReadOnlyList<string> _localDomains;
    private readonly DomainTracker? _tracker;
    private readonly ILogger<DnsProxy> _logger;
    private readonly bool _stripV6;

    /// <summary>
    /// ctor. Binds a loopback DNS endpoint, falling back to an alternative IPv4 loopback alias when
    /// the primary 127.0.0.1:53 is already taken. Never throws: when nothing can bind, <see
    /// cref="BoundV4"/> stays null and the caller degrades (connect without DNS interception).
    /// <paramref name="tunnelUpstream"/> resolves matched (to-be-tunneled) names - a clean resolver
    /// reached through the tunnel, so geo-blocked domains get their real IPs rather than the local
    /// network's poisoned answer; <paramref name="localUpstream"/> resolves everything else.
    /// </summary>
    public DnsProxy(IReadOnlyList<GeoDomain> domains, IPAddress tunnelUpstream, IPAddress localUpstream, IPAddress? lanUpstream, IReadOnlyList<string> localDomains, DomainTracker? tracker, ILogger<DnsProxy> logger, bool stripV6)
    {
        _domains = domains;
        _tunnelUpstream = tunnelUpstream;
        _localUpstream = localUpstream;
        _lanUpstream = lanUpstream;
        _localDomains = [.. localDomains.Select(d => d.Trim().Trim('.').ToLowerInvariant()).Where(d => d.Length > 0)];
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

        // DIAG: surface the proxy's effective state so a "geosite doesn't route" report can be diagnosed
        // from the log (did it bind? how many domains? which upstreams?).
        _logger.LogInformation("DIAG dnsproxy started: domains={Domains} tunnelUp={TunnelUp} localUp={LocalUp} lanUp={LanUp} localDomains={LocalDomains} v4={V4} v6={V6} stripV6={StripV6}",
            _domains.Count, _tunnelUpstream, _localUpstream, _lanUpstream is null ? "(none)" : _lanUpstream, _localDomains.Count, BoundV4, BoundV6, _stripV6);
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

    /// <summary>
    /// Drops all cached answers. Called once the tunnel is up so any answer cached during the bring-up
    /// window - before the clean resolver's /32 route was live, when a matched (geo-blocked) name could
    /// leak to the local network's poisoned resolver and be cached (e.g. chatgpt.com -> a sinkhole IP) -
    /// is discarded and re-resolved cleanly through the tunnel.
    /// </summary>
    public void ClearCache()
    {
        _cache.Clear();
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

            // A local/LAN name (built-in suffix, single-label host, reverse-DNS for a private range, or a
            // user exclusion entry) resolves via the LAN resolver and is answered as-is - no AAAA/HTTPS
            // deny, no tunnel routing - so the local network keeps working even in full tunnel, where every
            // other name would otherwise be forced offshore.
            var isLocal = name is not null && _lanUpstream is not null && IsLocalName(name);

            // A matched (to-be-tunneled) name resolves via the clean tunnel resolver so a geo-blocked
            // domain gets its real IPs instead of the local network's poisoned/blocked answer; everything
            // else uses the local resolver so coexisting / corporate names keep resolving.
            var matched = !isLocal && name is not null && DomainMatcher.IsTunneled(name, _domains);

            // DIAG: log only geo-MATCHED queries (the domains the user configured to route) so a
            // "X not routed" report can be localised, without logging every name the system resolves.
            if (matched && name is not null)
            {
                _logger.LogDebug("DIAG dns matched {Name} type={Type} -> tunnel {Up}", name, type, _tunnelUpstream);
            }

            byte[] response;
            if (!isLocal && _stripV6 && type == TypeAaaa)
            {
                // IPv4-only tunnel: return NODATA for AAAA so clients use IPv4 instead of stalling
                // on IPv6 addresses that route into a tunnel with no IPv6 transit.
                response = DnsMessage.BuildNoData(query);
            }
            else if (!isLocal && type == TypeHttps)
            {
                // HTTPS/SVCB records carry ipv4hint/ipv6hint addresses that we cannot intercept and
                // route into the tunnel. Honoring them lets clients (Chrome) connect straight to those
                // hints over HTTP/3, bypassing geo routing - for a geo-blocked destination that is a
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
                var upstream = isLocal ? _lanUpstream! : (matched ? _tunnelUpstream : _localUpstream);
                response = Forward(query, upstream);
                StoreInCache(name, type, response);
            }

            // Install routing for a matched domain BEFORE answering. Otherwise the client receives the
            // IP and opens a connection before the tunnel route + allowed-ip exist, so its first SYN
            // egresses off-tunnel and (for a blocked destination) is dropped - costing a multi-second
            // TCP retransmit on every freshly resolved domain. A tracking failure must never withhold
            // the answer, so it is isolated.
            if (matched)
            {
                try
                {
                    Track(name!, response);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "tracking matched domain failed");
                }
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

    // Whether a queried name should be resolved by the LAN resolver and kept off the tunnel: a bare
    // single-label host (nas, router), reverse-DNS for a private range, a built-in local suffix, or a
    // user exclusion-list domain. Matched as a dotted suffix so "x.corp.local" matches "corp.local".
    private bool IsLocalName(string name)
    {
        var n = name.TrimEnd('.').ToLowerInvariant();
        if (n.Length == 0)
        {
            return false;
        }

        if (!n.Contains('.'))
        {
            return true; // single-label intranet hostname
        }

        if (n.EndsWith(".in-addr.arpa", StringComparison.Ordinal))
        {
            // Only PRIVATE-range reverse-DNS goes to the LAN resolver; a PTR for a public address must not
            // bypass the tunnel resolver. (IPv6 reverse zones are left to the normal path - v6 is denied.)
            return IsPrivateReverseV4(n);
        }

        foreach (var suffix in BuiltinLocalSuffixes.Concat(_localDomains))
        {
            if (n == suffix || n.EndsWith("." + suffix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    // "d.c.b.a.in-addr.arpa" encodes the address a.b.c.d; treat only RFC1918 / link-local reverse zones
    // (including partial zones like "10.in-addr.arpa") as local. The first octet is the last label.
    private static bool IsPrivateReverseV4(string name)
    {
        var body = name[..^".in-addr.arpa".Length].TrimEnd('.');
        var labels = body.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (labels.Length == 0 || !byte.TryParse(labels[^1], out var o1))
        {
            return false;
        }

        if (o1 == 10)
        {
            return true;
        }

        byte? o2 = labels.Length >= 2 && byte.TryParse(labels[^2], out var b) ? b : null;
        return o1 switch
        {
            192 => o2 == 168,
            172 => o2 is >= 16 and <= 31,
            169 => o2 == 254,
            _ => false,
        };
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

    private static byte[] Forward(byte[] query, IPAddress upstream)
    {
        using (var client = new UdpClient())
        {
            client.Client.ReceiveTimeout = UpstreamTimeoutMs;
            client.Send(query, query.Length, new IPEndPoint(upstream, 53));
            var remote = new IPEndPoint(IPAddress.Any, 0);
            return client.Receive(ref remote);
        }
    }

    private void Track(string name, byte[] response)
    {
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

    /// <summary>
    /// Proactively resolves the rule's resolvable hostnames through the tunnel resolver and installs their
    /// routes, so an app holding a pre-tunnel cached IP is tunnelled without a DNS query reaching the proxy.
    /// </summary>
    public async Task SeedRoutesAsync(CancellationToken ct)
    {
        if (_tracker is null)
        {
            return;
        }

        var tracker = _tracker;

        // Wait for the DB-cache warm start, then resolve ONLY the rule hosts it did not already restore. A
        // reconnect with a populated cache thus issues no eager DNS at all; a cold cache resolves each host
        // once, which repopulates the cache for next time. Domains the user actually visits are resolved on
        // demand by the live proxy path regardless, so this is purely a best-effort pre-seed for apps that
        // hold a pre-tunnel cached IP and never re-query (#67).
        await tracker.WarmStartCompleted.WaitAsync(ct);

        var hosts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in _domains)
        {
            if (entry.Kind is GeoDomainKind.Full or GeoDomainKind.Domain)
            {
                var host = entry.Value.Trim().Trim('.').ToLowerInvariant();
                if (host.Length > 0 && !tracker.IsTracked(host))
                {
                    hosts.Add(host);
                }
            }
        }

        if (hosts.Count == 0)
        {
            return;
        }

        using var gate = new SemaphoreSlim(8);
        await Task.WhenAll(hosts.Select(h => ResolveOneAsync(gate, tracker, h, ct)));
    }

    private async Task ResolveOneAsync(SemaphoreSlim gate, DomainTracker tracker, string host, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            for (var attempt = 0; attempt < 3 && !ct.IsCancellationRequested; attempt++)
            {
                if (attempt > 0)
                {
                    try
                    {
                        await Task.Delay(2000, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }

                try
                {
                    var ips = new List<IPAddress>();
                    CollectAddresses(host, 1, ips);
                    if (!_stripV6)
                    {
                        CollectAddresses(host, 28, ips);
                    }

                    if (ips.Count > 0)
                    {
                        tracker.Update(host, ips.Select(a => a.ToString()).ToList());
                        return;
                    }
                }
                catch (Exception)
                {
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private void CollectAddresses(string host, int type, List<IPAddress> ips)
    {
        var response = Forward(DnsMessage.BuildQuery(host, type), _tunnelUpstream);
        foreach (var ip in DnsMessage.Addresses(response))
        {
            ips.Add(ip);
        }
    }
}
