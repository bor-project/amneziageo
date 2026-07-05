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
/// Loopback DNS proxy that forwards queries through the tunnel and feeds resolved tunneled domains
/// to the domain tracker.
/// </summary>
internal sealed class DnsProxy
{
    private const int UpstreamTimeoutMs = 5000;
    // Per-attempt wait before retransmitting a lost upstream query, within the overall timeout.
    private const int UpstreamRetransmitMs = 400;
    private const int SioUdpConnReset = unchecked((int)0x9800000C);
    private const int TypeA = 1;
    private const int TypeAaaa = 28;
    private const int TypeHttps = 65; // HTTPS/SVCB
    private const int MinCacheSeconds = 10;
    private const int MaxCacheSeconds = 300;
    // Serve-known: TTL on an answer synthesized from tracked IPs. Short so the client re-asks and picks up
    // freshly revalidated IPs, but long enough that repeat queries take the lock-free cache path.
    private const int ServeKnownTtlSeconds = 30;
    // Minimum gap between background revalidations of the same domain, so a chatty client cannot storm the
    // (lossy) tunnel resolver.
    private const int RevalidateMinIntervalMs = 60_000;

    // Fallback loopback aliases when 127.0.0.1:53 is taken by another resolver.
    private static readonly IPAddress[] V4Candidates = [IPAddress.Loopback, IPAddress.Parse("127.0.0.2")];

    // Suffixes resolved via the LAN resolver and never tunneled.
    private static readonly string[] BuiltinLocalSuffixes =
        ["local", "lan", "home", "home.arpa", "internal", "intranet", "corp", "localdomain", "localhost"];

    private readonly List<UdpClient> _servers = [];
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    // Coalesces concurrent identical (name,type) misses onto a single upstream query.
    private readonly ConcurrentDictionary<string, Lazy<byte[]>> _inflight = new(StringComparer.Ordinal);
    // Per-name Environment.TickCount64 of the last background revalidate, to rate-limit them.
    private readonly ConcurrentDictionary<string, long> _lastRevalidate = new(StringComparer.Ordinal);
    // Bounds concurrent background revalidations so a burst of first-served domains cannot park many pool
    // threads (each Forward blocks up to UpstreamTimeoutMs) and starve the Handle path.
    private readonly SemaphoreSlim _revalidateSlots = new(4, 4);
    // Volatile: read on the hot query path, replaced on the poll thread; the matcher is immutable.
    private volatile IReadOnlyList<GeoDomain> _domains;
    private volatile DomainMatcher _matcher;
    private readonly IPAddress _tunnelUpstream;
    private readonly IPAddress? _tunnelUpstreamSecondary;
    private readonly IPAddress _localUpstream;
    private readonly IPAddress? _lanUpstream;
    private readonly IReadOnlyList<string> _localDomains;
    private readonly DomainTracker? _tracker;
    private readonly ILogger<DnsProxy> _logger;
    private readonly bool _stripV6;

    /// <summary>
    /// ctor
    /// </summary>
    public DnsProxy(IReadOnlyList<GeoDomain> domains, IPAddress tunnelUpstream, IPAddress localUpstream, IPAddress? lanUpstream, IReadOnlyList<string> localDomains, DomainTracker? tracker, ILogger<DnsProxy> logger, bool stripV6, IPAddress? tunnelSecondary = null)
    {
        _domains = domains;
        _matcher = new DomainMatcher(domains);
        _tunnelUpstream = tunnelUpstream;
        _tunnelUpstreamSecondary = tunnelSecondary;
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

        _logger.LogInformation("DIAG dnsproxy started: domains={Domains} tunnelUp={TunnelUp} tunnelUp2={TunnelUp2} localUp={LocalUp} lanUp={LanUp} localDomains={LocalDomains} v4={V4} v6={V6} stripV6={StripV6}",
            _domains.Count, _tunnelUpstream, _tunnelUpstreamSecondary is null ? "(none)" : _tunnelUpstreamSecondary, _localUpstream, _lanUpstream is null ? "(none)" : _lanUpstream, _localDomains.Count, BoundV4, BoundV6, _stripV6);
    }

    /// <summary>
    /// The IPv4 loopback address the proxy bound, or null.
    /// </summary>
    public IPAddress? BoundV4 { get; }

    /// <summary>
    /// The IPv6 loopback address the proxy bound, or null.
    /// </summary>
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
    /// Drops all cached answers.
    /// </summary>
    public void ClearCache()
    {
        _cache.Clear();
    }

    /// <summary>
    /// Rebuilds the domain matcher live from a refreshed rule set.
    /// </summary>
    public void UpdateDomains(IReadOnlyList<GeoDomain> domains, CancellationToken ct)
    {
        var previous = _domains;
        _matcher = new DomainMatcher(domains);
        _domains = domains;

        // Drop cached answers: a newly matched name may have a pre-match entry from the local resolver.
        _cache.Clear();

        _logger.LogInformation("geo cache: domain matcher rebuilt live ({Count} rule(s))", domains.Count);
        if (RouteLog.Enabled)
        {
            RouteLog.Note($"matcher rebuilt live: {domains.Count} rule(s)");
        }

        if (_tracker is not null && !ct.IsCancellationRequested)
        {
            // Actualization: drop domains that left the lists, then seed the ones that were added.
            PruneDepartedDomains();
            _ = SeedNewDomainsAsync(previous, domains, ct);
        }
    }

    // Removes tracked domains that no longer match any current routing rule. Union semantics of the
    // materialized set mean a domain contributed by several rules/categories survives until the LAST
    // one drops it - so "youtube in 3 lists" is only untracked when none of them list it anymore.
    private void PruneDepartedDomains()
    {
        var tracker = _tracker;
        var matcher = _matcher;
        if (tracker is null)
        {
            return;
        }

        var removed = 0;
        foreach (var host in tracker.TrackedHosts())
        {
            if (!matcher.IsTunneled(host))
            {
                tracker.Remove(host);
                _lastRevalidate.TryRemove(host, out _);
                removed++;
            }
        }

        if (removed > 0)
        {
            _logger.LogInformation("geo cache: dropped {Count} domain(s) no longer in the routing lists", removed);
            if (RouteLog.Enabled)
            {
                RouteLog.Note($"prune: dropped {removed} departed domain(s)");
            }
        }
    }

    // Best-effort resolve+route of hosts newly added since the previous matcher build.
    private async Task SeedNewDomainsAsync(IReadOnlyList<GeoDomain> previous, IReadOnlyList<GeoDomain> current, CancellationToken ct)
    {
        var tracker = _tracker;
        if (tracker is null)
        {
            return;
        }

        var old = new HashSet<string>(RuleHosts(previous), StringComparer.Ordinal);
        var added = new HashSet<string>(StringComparer.Ordinal);
        foreach (var host in RuleHosts(current))
        {
            if (!old.Contains(host) && !tracker.IsTracked(host))
            {
                added.Add(host);
            }
        }

        if (added.Count == 0)
        {
            return;
        }

        _logger.LogInformation("geo cache: pre-resolving {Count} newly added domain(s) live", added.Count);
        using var gate = new SemaphoreSlim(8);
        try
        {
            await Task.WhenAll(added.Select(h => ResolveOneAsync(gate, tracker, h, ct)));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // Fire-and-forget; the matcher is already swapped.
            _logger.LogDebug(ex, "live pre-resolve of newly added domains failed");
        }
    }

    // Resolvable rule hosts (full/domain) of a materialized domain set, normalized.
    private static IEnumerable<string> RuleHosts(IReadOnlyList<GeoDomain> domains)
    {
        foreach (var entry in domains)
        {
            if (entry.Kind is GeoDomainKind.Full or GeoDomainKind.Domain)
            {
                var host = entry.Value.Trim().Trim('.').ToLowerInvariant();
                if (host.Length > 0)
                {
                    yield return host;
                }
            }
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

            // Local/LAN names resolve via the LAN resolver and stay off the tunnel.
            var isLocal = name is not null && _lanUpstream is not null && IsLocalName(name);

            // Matched names resolve via the clean tunnel resolver; others use the local resolver.
            var geoMatch = !isLocal && name is not null ? _matcher.Match(name) : null;
            var matched = geoMatch is not null;

            // Per-request trace; matched subset at debug for diagnosability.
            if (name is not null)
            {
                var route = isLocal ? "lan" : matched ? "tunnel" : "local";
                _logger.LogTrace("dns query {Name} type={Type} -> {Route}", name, type, route);
                if (matched)
                {
                    _logger.LogDebug("dns matched {Name} type={Type} -> tunnel {Up}", name, type, _tunnelUpstream);
                }
            }

            byte[] response;
            var fromCache = false;
            if (!isLocal && _stripV6 && type == TypeAaaa)
            {
                // IPv4-only tunnel: NODATA for AAAA so clients use IPv4.
                response = DnsMessage.BuildNoData(query);
            }
            else if (!isLocal && type == TypeHttps)
            {
                // Deny HTTPS/SVCB records: their hint addresses bypass the tunnel.
                response = DnsMessage.BuildNoData(query);
            }
            else if (TryGetCached(name, type, query, out var cached))
            {
                response = cached;
                fromCache = true;
            }
            else if (matched && type == TypeA && _tracker is not null && _tracker.KnownIps(name!) is { Count: > 0 } known)
            {
                // Already-tracked domain: its IPs are installed as /32 routes and carrying traffic. Answer
                // instantly from that last-good set instead of re-querying the tunnel resolver, which rides
                // the same lossy underlay and would stall the client for seconds. Refresh in the background
                // to pick up new CDN IPs. Cache the synthetic answer (short TTL) so repeat queries take the
                // lock-free cache path, and treat it as already-handled so the route step below is skipped
                // (routes for a known domain are already installed).
                response = DnsMessage.BuildAAnswer(query, known, ServeKnownTtlSeconds);
                fromCache = true;
                StoreInCache(name, type, response);
                TriggerRevalidate(name!);
            }
            else
            {
                var upstream = isLocal ? _lanUpstream! : (matched ? _tunnelUpstream : _localUpstream);
                var secondary = matched ? _tunnelUpstreamSecondary : null;
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                var result = ForwardCoalesced(name, type, query, upstream, secondary);
                if (result.Error is not null)
                {
                    // Upstream unreachable: warn so a 'site won't open' report shows DNS failed.
                    var route = isLocal ? "lan" : matched ? "tunnel" : "local";
                    _logger.LogWarning("dns query {Name} type={Type} -> {Route} unreachable: {Reason}", name, type, route, result.Error.Message);
                    if (RouteLog.Enabled && name is not null && result.Leader)
                    {
                        RouteLog.Note(FormatRouteQuery(name, type, isLocal, matched, geoMatch, upstream, started, ips: null, failure: result.Error.Message));
                    }

                    // Answer SERVFAIL instead of dropping the query, so the client fails fast and
                    // retries at once rather than waiting out its own multi-second resolver timeout.
                    var servfail = DnsMessage.BuildServFail(query);
                    lock (server)
                    {
                        server.Send(servfail, servfail.Length, client);
                    }

                    return;
                }

                var shared = result.Response!;
                StoreInCache(name, type, shared);
                // Followers share the leader's buffer; answer each client with its own transaction id.
                response = ApplyTransactionId(shared, query);

                // Routing-log line for a real resolution, written only by the coalescing leader.
                if (RouteLog.Enabled && name is not null && result.Leader)
                {
                    var ips = DnsMessage.Addresses(shared).Select(a => a.ToString()).ToList();
                    RouteLog.Note(FormatRouteQuery(name, type, isLocal, matched, geoMatch, upstream, started, ips, failure: null));
                }
            }

            // Route a matched domain before answering, or the client's first SYN egresses off-tunnel.
            // Tracking failure is isolated so the answer still goes out; cache hits were already tracked.
            if (matched && !fromCache)
            {
                try
                {
                    Track(name!, response);
                }
                catch (Exception ex)
                {
                    // Route installation failed; the matched domain won't route through the tunnel.
                    _logger.LogWarning(ex, "routing matched domain {Name} failed (route not installed)", name);
                    if (RouteLog.Enabled)
                    {
                        RouteLog.Note($"route FAILED for {name}: {ex.Message}");
                    }
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

    // Refreshes a tracked domain's IPs off the client's query path. Rate-limited per name and resolved
    // straight to the tunnel resolver (bypassing the serve-known branch above so new IPs are actually
    // discovered). A lost query is fine - the domain keeps serving its last-good set and we retry next window.
    private void TriggerRevalidate(string name)
    {
        var tracker = _tracker;
        if (tracker is null)
        {
            return;
        }

        var key = name.TrimEnd('.').ToLowerInvariant();
        var now = Environment.TickCount64;
        var last = _lastRevalidate.GetOrAdd(key, 0);
        if (now - last < RevalidateMinIntervalMs || !_lastRevalidate.TryUpdate(key, now, last))
        {
            return;
        }

        ThreadPool.QueueUserWorkItem(_ =>
        {
            if (!_revalidateSlots.Wait(0))
            {
                return; // too many revalidations in flight; the per-name window retries this later
            }

            try
            {
                var response = Forward(DnsMessage.BuildQuery(name, TypeA), _tunnelUpstream, _tunnelUpstreamSecondary);
                var ips = DnsMessage.Addresses(response)
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => a.ToString())
                    .ToList();
                if (ips.Count > 0)
                {
                    tracker.Add(name, ips);
                }
            }
            catch
            {
                // Background refresh; a lost query just means we try again on the next window.
            }
            finally
            {
                _revalidateSlots.Release();
            }
        });
    }

    // Whether a name resolves via the LAN resolver and stays off the tunnel.
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
            // Only private-range reverse-DNS goes to the LAN resolver; IPv6 reverse zones use the normal path.
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

    // Only RFC1918 / link-local reverse zones are local.
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

        var key = CacheKey(name, type);
        if (_cache.TryGetValue(key, out var entry))
        {
            if (entry.Expiry > DateTime.UtcNow)
            {
                response = (byte[])entry.Response.Clone();
                if (response.Length >= 2 && query.Length >= 2)
                {
                    response[0] = query[0];
                    response[1] = query[1];
                }

                return true;
            }

            // Drop expired entry; a concurrently-refreshed newer entry is left intact.
            _cache.TryRemove(new KeyValuePair<string, CacheEntry>(key, entry));
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
            return;
        }

        var seconds = Math.Clamp(ttl, MinCacheSeconds, MaxCacheSeconds);
        _cache[CacheKey(name, type)] = new CacheEntry((byte[])response.Clone(), DateTime.UtcNow.AddSeconds(seconds));
    }

    private static string CacheKey(string name, int type)
    {
        return type.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + name.TrimEnd('.').ToLowerInvariant();
    }

    private static byte[] Forward(byte[] query, IPAddress upstream, IPAddress? secondary = null)
    {
        var upstreams = secondary is null || secondary.Equals(upstream)
            ? new[] { upstream }
            : new[] { upstream, secondary };
        var deadlineMs = Environment.TickCount64 + UpstreamTimeoutMs;
        SocketException? last = null;
        var idx = 0;
        var missesOnCurrent = 0;
        while (true)
        {
            var remaining = (int)(deadlineMs - Environment.TickCount64);
            if (remaining <= 0)
            {
                throw last ?? new SocketException((int)SocketError.TimedOut);
            }

            // Retransmit a dropped query after a short wait rather than stalling the whole budget
            // on one lost packet; a fresh socket per attempt lets us fail over between resolvers.
            var attemptMs = Math.Min(remaining, UpstreamRetransmitMs);
            var attemptStart = Environment.TickCount64;
            try
            {
                using var client = new UdpClient();
                client.Client.ReceiveTimeout = attemptMs;
                client.Connect(new IPEndPoint(upstreams[idx], 53));
                client.Send(query, query.Length);
                var remote = new IPEndPoint(IPAddress.Any, 0);
                return client.Receive(ref remote);
            }
            catch (SocketException ex) when (
                ex.SocketErrorCode is SocketError.TimedOut
                or SocketError.HostUnreachable
                or SocketError.NetworkUnreachable
                or SocketError.ConnectionReset)
            {
                last = ex;
                // Fail over to the secondary resolver after two misses so a resolver blackhole
                // recovers, not just an occasional dropped datagram (retransmit handles that).
                if (upstreams.Length > 1 && ++missesOnCurrent >= 2)
                {
                    idx = (idx + 1) % upstreams.Length;
                    missesOnCurrent = 0;
                }

                // Pace retransmits when the failure returns faster than the window (ICMP
                // unreachable/reset), so bring-up churn doesn't spin the loop.
                var pause = attemptMs - (int)(Environment.TickCount64 - attemptStart);
                if (pause > 0)
                {
                    Thread.Sleep(pause);
                }
            }
        }
    }

    // Runs the upstream query once per in-flight (name,type); only the leader writes the routing-log line.
    private CoalescedResult ForwardCoalesced(string? name, int type, byte[] query, IPAddress upstream, IPAddress? secondary = null)
    {
        if (name is null)
        {
            try
            {
                return new CoalescedResult(Forward(query, upstream, secondary), Leader: true, Error: null);
            }
            catch (Exception ex)
            {
                return new CoalescedResult(Response: null, Leader: true, ex);
            }
        }

        var key = CacheKey(name, type);
        // GetOrAdd(key, value): the caller whose instance is stored is the leader.
        var mine = new Lazy<byte[]>(() => Forward(query, upstream, secondary), LazyThreadSafetyMode.ExecutionAndPublication);
        var lazy = _inflight.GetOrAdd(key, mine);
        var leader = ReferenceEquals(lazy, mine);
        try
        {
            return new CoalescedResult(lazy.Value, leader, Error: null);
        }
        catch (Exception ex)
        {
            return new CoalescedResult(Response: null, leader, ex);
        }
        finally
        {
            // Remove only our own entry so a racing newcomer's fresh Lazy is left intact.
            _inflight.TryRemove(new KeyValuePair<string, Lazy<byte[]>>(key, lazy));
        }
    }

    // Outcome of a coalesced forward: response, leader flag, and failure.
    private readonly record struct CoalescedResult(byte[]? Response, bool Leader, Exception? Error);

    // Returns a copy of the upstream response carrying the caller's transaction id.
    private static byte[] ApplyTransactionId(byte[] response, byte[] query)
    {
        var copy = (byte[])response.Clone();
        if (copy.Length >= 2 && query.Length >= 2)
        {
            copy[0] = query[0];
            copy[1] = query[1];
        }

        return copy;
    }

    // Routing-log line: resolved addresses, upstream, matched rule, round-trip time.
    private static string FormatRouteQuery(string name, int type, bool isLocal, bool matched, DomainMatcher.GeoMatch? geoMatch, IPAddress upstream, long startedTimestamp, IReadOnlyList<string>? ips, string? failure)
    {
        var ms = (long)System.Diagnostics.Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds;
        var decision = isLocal ? "LAN" : matched ? "TUNNEL" : "LOCAL";
        var rule = matched && geoMatch is { } gm ? "  rule=" + RuleLabel(gm) : string.Empty;
        if (failure is not null)
        {
            return $"{name} {TypeLabel(type)} -> {decision}  FAILED  up={upstream}  {ms}ms{rule}  {failure}";
        }

        var ipText = ips is null || ips.Count == 0
            ? "-"
            : ips.Count <= 6 ? string.Join(",", ips) : string.Join(",", ips.Take(6)) + $" +{ips.Count - 6} more";
        return $"{name} {TypeLabel(type)} -> {decision}  ip={ipText}  up={upstream}  {ms}ms{rule}";
    }

    // DNS record type -> short label for the routing log; unknown types fall back to "typeNN".
    private static string TypeLabel(int type) => type switch
    {
        1 => "A",
        28 => "AAAA",
        65 => "HTTPS",
        5 => "CNAME",
        12 => "PTR",
        15 => "MX",
        16 => "TXT",
        33 => "SRV",
        2 => "NS",
        6 => "SOA",
        _ => "type" + type.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    // The matched geo rule as "<kind>:<value>" (e.g. "domain:openai.com").
    private static string RuleLabel(DomainMatcher.GeoMatch match) => match.Kind switch
    {
        GeoDomainKind.Full => "full:" + match.Value,
        GeoDomainKind.Domain => "domain:" + match.Value,
        GeoDomainKind.Plain => "plain:" + match.Value,
        GeoDomainKind.Regex => "regex:" + match.Value,
        _ => match.Value,
    };

    private void Track(string name, byte[] response)
    {
        var ips = new List<string>();
        foreach (var ip in DnsMessage.Addresses(response))
        {
            ips.Add(ip.ToString());
        }

        // Re-check membership at Add time: _matcher may have swapped (a list edit) between the match that
        // routed this query here and now - do not (re-)route a domain that just left the routing lists.
        if (ips.Count > 0 && _matcher.IsTunneled(name))
        {
            // Hot path: add-only union with the cache; a partial answer never drops a working IP.
            _tracker?.Add(name, ips);
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

        // Resolve only rule hosts the DB-cache warm start did not already restore.
        await tracker.WarmStartCompleted.WaitAsync(ct);

        var hosts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var host in RuleHosts(_domains))
        {
            if (!tracker.IsTracked(host))
            {
                hosts.Add(host);
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
                        // Seed/pre-resolve is add-only. Re-check membership at Add time: a long seed retry can
                        // complete after a later list edit dropped this host (and after PruneDepartedDomains
                        // ran), which would otherwise re-install a zombie route for a departed domain.
                        if (_matcher.IsTunneled(host))
                        {
                            tracker.Add(host, ips.Select(a => a.ToString()).ToList());
                        }

                        return;
                    }
                }
                catch (Exception)
                {
                }
            }

            // All attempts exhausted without an answer; the rule host could not be pre-resolved.
            if (!ct.IsCancellationRequested)
            {
                _logger.LogWarning("seed: rule host {Host} unreachable through the tunnel resolver", host);
                if (RouteLog.Enabled)
                {
                    RouteLog.Note($"seed UNREACHABLE {host}");
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
        var response = Forward(DnsMessage.BuildQuery(host, type), _tunnelUpstream, _tunnelUpstreamSecondary);
        foreach (var ip in DnsMessage.Addresses(response))
        {
            ips.Add(ip);
        }
    }
}
