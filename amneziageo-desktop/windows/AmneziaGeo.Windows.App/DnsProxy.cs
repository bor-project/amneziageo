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
    // Per-attempt wait before retransmitting a lost upstream query, within the overall timeout. Kept short
    // because the tunnel resolver rides a lossy underlay: a dropped datagram should recover in well under a
    // human-perceptible pause, not a near-half-second window.
    private const int UpstreamRetransmitMs = 250;
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
    // Negative cache: how long a name proven to match NO geo rule is remembered as "bypass" so the matcher
    // isn't re-run on every query. Bounded, and cleared wholesale on a matcher rebuild (list edit), so a
    // domain newly added to a list is re-evaluated promptly.
    private const int BypassTtlSeconds = 600;
    // Reachability probe (serve-known heal): a short TCP handshake to a domain's last-good IPs decides whether
    // the cached set still connects BEFORE any re-resolve. 443 is the near-universal port for tunneled web/CDN
    // hosts; a completed handshake or a refusal (RST) both prove the path+host are alive, only silence is dead.
    // The re-resolve is gated on this failing, so a working IP is left pinned instead of churned every window.
    private const int ProbePort = 443;
    private const int ProbeTimeoutMs = 3000;
    private const int MaxProbeIps = 3;

    // Fallback loopback aliases when 127.0.0.1:53 is taken by another resolver.
    private static readonly IPAddress[] V4Candidates = [IPAddress.Loopback, IPAddress.Parse("127.0.0.2")];

    // Suffixes resolved via the LAN resolver and never tunneled.
    private static readonly string[] BuiltinLocalSuffixes =
        ["local", "lan", "home", "home.arpa", "internal", "intranet", "corp", "localdomain", "localhost"];

    private readonly List<UdpClient> _servers = [];
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    // Coalesces concurrent identical (name,type) misses onto a single upstream query.
    private readonly ConcurrentDictionary<string, Lazy<Task<byte[]>>> _inflight = new(StringComparer.Ordinal);
    // Per-name Environment.TickCount64 of the last background revalidate, to rate-limit them.
    private readonly ConcurrentDictionary<string, long> _lastRevalidate = new(StringComparer.Ordinal);
    // Names proven to match no geo rule -> resolved locally and never tunneled/re-resolved. Value is the
    // Environment.TickCount64 expiry. Short-circuits the matcher on repeat queries; cleared on matcher rebuild.
    private readonly ConcurrentDictionary<string, long> _bypass = new(StringComparer.Ordinal);
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
    // All LAN resolvers; a multi-provider box races them and takes the first answer with records so a
    // censoring provider's NXDOMAIN is passed over.
    private readonly IReadOnlyList<IPAddress> _lanPool;
    // Non-geo names resolve on the LAN (raceable) in split mode; offshore through the tunnel in full mode.
    private readonly bool _localIsLan;
    private readonly IReadOnlyList<string> _localDomains;
    private readonly DomainTracker? _tracker;
    private readonly ILogger<DnsProxy> _logger;
    private readonly bool _stripV6;

    /// <summary>
    /// ctor
    /// </summary>
    public DnsProxy(IReadOnlyList<GeoDomain> domains, IPAddress tunnelUpstream, IPAddress localUpstream, IPAddress? lanUpstream, IReadOnlyList<IPAddress> lanPool, bool localIsLan, IReadOnlyList<string> localDomains, DomainTracker? tracker, ILogger<DnsProxy> logger, bool stripV6, IPAddress? tunnelSecondary = null)
    {
        _domains = domains;
        _matcher = new DomainMatcher(domains);
        _tunnelUpstream = tunnelUpstream;
        _tunnelUpstreamSecondary = tunnelSecondary;
        _localUpstream = localUpstream;
        _lanUpstream = lanUpstream;
        _lanPool = lanPool;
        _localIsLan = localIsLan;
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

        _logger.LogInformation("DIAG dnsproxy started: domains={Domains} tunnelUp={TunnelUp} tunnelUp2={TunnelUp2} localUp={LocalUp} lanUp={LanUp} lanPool={LanPool} localDomains={LocalDomains} v4={V4} v6={V6} stripV6={StripV6}",
            _domains.Count, _tunnelUpstream, _tunnelUpstreamSecondary is null ? "(none)" : _tunnelUpstreamSecondary, _localUpstream, _lanUpstream is null ? "(none)" : _lanUpstream, string.Join(",", _lanPool), _localDomains.Count, BoundV4, BoundV6, _stripV6);
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
        // DNS forwarding (Forward) is synchronous and blocks a pool thread for up to UpstreamTimeoutMs on a
        // lossy/dead resolver. Each query is dispatched via ThreadPool (see ServeOne), so a burst of
        // cache-missing lookups (e.g. flipping YouTube Shorts, which use fresh per-video CDN hostnames)
        // parks many pool threads at once. At the default min (= CPU count) the pool then injects new
        // threads only ~1-2/sec, so fast answers (cache hits, serve-known) queue for seconds behind the
        // blocked forwards. Raise the min so the burst is absorbed without the injection throttle; threads
        // are still created only on demand.
        ThreadPool.GetMinThreads(out var minWorker, out var minIo);
        ThreadPool.SetMinThreads(Math.Max(minWorker, 128), minIo);

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
        _bypass.Clear();
    }

    // Whether a name is currently negative-cached as matching no geo rule.
    private bool IsBypassed(string name)
    {
        var key = name.TrimEnd('.').ToLowerInvariant();
        if (_bypass.TryGetValue(key, out var expiry))
        {
            if (expiry > Environment.TickCount64)
            {
                return true;
            }

            _bypass.TryRemove(new KeyValuePair<string, long>(key, expiry));
        }

        return false;
    }

    // Records a name as matching no geo rule, so the matcher is skipped until the entry expires or the lists change.
    private void MarkBypassed(string name)
    {
        var key = name.TrimEnd('.').ToLowerInvariant();
        _bypass[key] = Environment.TickCount64 + (BypassTtlSeconds * 1000L);
    }

    /// <summary>
    /// Rebuilds the domain matcher live from a refreshed rule set. Returns true when the set gained a domain,
    /// so the caller can flush the OS resolver cache and force clients to re-query through the proxy.
    /// </summary>
    public bool UpdateDomains(IReadOnlyList<GeoDomain> domains, CancellationToken ct)
    {
        var previous = _domains;
        _matcher = new DomainMatcher(domains);
        _domains = domains;

        // Drop cached answers: a newly matched name may have a pre-match entry from the local resolver.
        _cache.Clear();
        // Drop negative-cache entries: a name previously bypassed may now be in a rule (or vice versa).
        _bypass.Clear();

        _logger.LogInformation("geo cache: domain matcher rebuilt live ({Count} rule(s))", domains.Count);
        if (RouteLog.Enabled)
        {
            RouteLog.Note($"matcher rebuilt live: {domains.Count} rule(s)");
        }

        var addedNew = HasAddedDomains(previous, domains);
        if (_tracker is not null && !ct.IsCancellationRequested)
        {
            // Actualization: drop domains that left the lists, then seed the ones that were added.
            PruneDepartedDomains();
            _ = SeedNewDomainsAsync(previous, domains, ct);
        }

        return addedNew;
    }

    // True when the new set contains a domain (any kind) absent from the previous set.
    private static bool HasAddedDomains(IReadOnlyList<GeoDomain> previous, IReadOnlyList<GeoDomain> current)
    {
        var old = new HashSet<string>(previous.Select(DomainKey), StringComparer.Ordinal);
        return current.Any(d => !old.Contains(DomainKey(d)));
    }

    private static string DomainKey(GeoDomain domain) => string.Concat(domain.Kind.ToString(), "|", domain.Value);

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
                    _ = HandleAsync(server, query, client);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "dns proxy stopped on {Address}", anyEndpoint);
        }
    }

    private async Task HandleAsync(UdpClient server, byte[] query, IPEndPoint client)
    {
        try
        {
            var name = DnsMessage.QuestionName(query);
            var type = DnsMessage.QuestionType(query);

            // Local/LAN names resolve via the LAN resolver and stay off the tunnel.
            var isLocal = name is not null && _lanUpstream is not null && IsLocalName(name);

            // Negative cache: a name already proven to be in no geo rule bypasses the matcher and the tunnel.
            var bypassed = name is not null && IsBypassed(name);

            // Matched names resolve via the clean tunnel resolver; others use the local resolver.
            var geoMatch = !isLocal && !bypassed && name is not null ? _matcher.Match(name) : null;
            var matched = geoMatch is not null;

            // Remember a non-local miss so the matcher isn't re-run for it until the lists change.
            if (name is not null && !isLocal && !bypassed && !matched)
            {
                MarkBypassed(name);
            }

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
                // the same lossy underlay and would stall the client for seconds. In the background, probe
                // that set's reachability and re-resolve ONLY if it is dead - a working CDN IP is left pinned
                // (no churn on the lossy resolver). Cache the synthetic answer (short TTL) so repeat queries
                // take the lock-free cache path, and treat it as already-handled so the route step below is
                // skipped (routes for a known domain are already installed).
                response = DnsMessage.BuildAAnswer(query, known, ServeKnownTtlSeconds);
                fromCache = true;
                StoreInCache(name, type, response);
                TriggerReachabilityRefresh(name!, known);
            }
            else if (matched && type == TypeA && _tracker is not null
                     && await _tracker.TryHydrateFromCacheAsync(name!, n => _matcher.IsTunneled(n)).ConfigureAwait(false) is { Count: > 0 } hydrated)
            {
                // Not in memory but cached in the DB from an earlier session: restore that last-good set and its
                // routes without hitting the (lossy) tunnel resolver, then background-probe it as with a
                // serve-known hit. The hydrate installed the routes, so the Track step below is skipped.
                response = DnsMessage.BuildAAnswer(query, hydrated, ServeKnownTtlSeconds);
                fromCache = true;
                StoreInCache(name, type, response);
                TriggerReachabilityRefresh(name!, hydrated);
            }
            else
            {
                var upstream = isLocal ? _lanUpstream! : (matched ? _tunnelUpstream : _localUpstream);
                var secondary = matched ? _tunnelUpstreamSecondary : null;
                // LAN-bound names (local, or non-geo in split) race the whole provider pool.
                var lanRace = _lanPool.Count > 1 && (isLocal || (!matched && _localIsLan));
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                var result = lanRace
                    ? await ForwardCoalescedRacedAsync(name, type, query)
                    : await ForwardCoalescedAsync(name, type, query, upstream, secondary);
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

    // Schedules a reachability-gated refresh off the client's query path. Rate-limited per name so a chatty
    // client cannot storm the probe/resolver. The refresh only re-resolves when the last-good set is proven
    // dead - a working set is left pinned (this is the optimization over the old blind per-window re-resolve).
    private void TriggerReachabilityRefresh(string name, IReadOnlyList<string> ips)
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

        _ = ReachabilityRefreshAsync(name, ips, tracker);
    }

    // Off the query path: probe the domain's last-good IPs and re-resolve ONLY if none is reachable through
    // the tunnel. A live probe means the cached value still connects, so we resolve nothing (no churn on the
    // lossy resolver). If the set looks dead, we DON'T evict blindly - first we re-resolve, and that query
    // doubles as a connectivity check: if the resolver itself is unreachable the whole tunnel path is down
    // (not these IPs), so we keep the cached set and evict nothing. Only when the resolver answers with a
    // DIFFERENT set is the edge genuinely dead -> Replace (evict dead, install fresh, rebuild allowed-ips =
    // the "save") and drop the synthetic serve-known entry. If the resolver re-confirms the SAME set, the
    // probe miss was transient and we leave it pinned. This guards against erroneous eviction/re-resolve when
    // connectivity - not the address - is what dropped. Bounded by _revalidateSlots; async so a lossy
    // probe/resolve parks a Task, not a pool thread.
    private async Task ReachabilityRefreshAsync(string name, IReadOnlyList<string> ips, DomainTracker tracker)
    {
        if (!await _revalidateSlots.WaitAsync(0).ConfigureAwait(false))
        {
            return; // too many refreshes in flight; the per-name window retries this later
        }

        try
        {
            if (await ProbeAnyReachableAsync(ips).ConfigureAwait(false))
            {
                return; // cached value still connects - no re-resolve
            }

            // The last-good set did not answer. Don't evict it yet - re-resolve through the tunnel first, and
            // let that query double as a connectivity check. ForwardAsync throws when the resolver/path is
            // unreachable, i.e. the whole tunnel is down rather than these IPs being dead; in that case we keep
            // the cached set and evict nothing (a transient outage must never blackhole a live domain).
            byte[] response;
            try
            {
                response = await ForwardAsync(DnsMessage.BuildQuery(name, TypeA), _tunnelUpstream, _tunnelUpstreamSecondary).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug("reachability heal {Name}: resolver unreachable ({Reason}) - path down, keeping cached set", name, ex.Message);
                return;
            }

            var fresh = DnsMessage.Addresses(response)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.ToString())
                .ToList();
            if (fresh.Count == 0)
            {
                return; // resolver answered but gave no A records: keep the old set (never blackhole)
            }

            // Connectivity is proven (the resolver answered), yet it still hands out the same IPs the probe
            // just missed: the miss was transient (congestion/underlay drop), the set is not actually dead.
            // Evicting/rebuilding here would be the erroneous deletion + churn we want to avoid.
            if (SameV4Set(ips, fresh))
            {
                _logger.LogDebug("reachability heal {Name}: resolver re-confirms same set - transient probe miss, no evict", name);
                return;
            }

            // Genuinely dead: the resolver moved the domain to a different set. Replace evicts the dropped/dead
            // IPs and installs the fresh ones - this is the save of the new value.
            tracker.Replace(name, fresh);

            // Drop the short synthetic serve-known answer so the next client query serves the fresh set at
            // once instead of the now-dead IP for up to ServeKnownTtlSeconds.
            _cache.TryRemove(CacheKey(name, TypeA), out _);

            _logger.LogInformation("reachability heal {Name}: last-good set unreachable -> re-resolved to {Ips}", name, string.Join(", ", fresh));
            if (RouteLog.Enabled)
            {
                RouteLog.Note($"heal {name.TrimEnd('.').ToLowerInvariant()}: dead set -> [{string.Join(",", fresh)}]");
            }
        }
        catch
        {
            // Background refresh; a lost probe/query just means we try again on the next window.
        }
        finally
        {
            _revalidateSlots.Release();
        }
    }

    // TCP reachability probe: a SYN to :443 on the last-good IPs through the tunnel (their /32s are already
    // routed there), racing the first few in parallel so a dead set fails fast within one short deadline. A
    // completed handshake OR a refusal (RST) both prove the path+host are alive; only silence (timeout /
    // unreachable) is dead. First live IP wins and cancels the rest.
    private static async Task<bool> ProbeAnyReachableAsync(IReadOnlyList<string> ips)
    {
        var targets = ips
            .Where(ip => IPAddress.TryParse(ip, out var a) && a.AddressFamily == AddressFamily.InterNetwork)
            .Take(MaxProbeIps)
            .Select(IPAddress.Parse)
            .ToList();
        if (targets.Count == 0)
        {
            return false;
        }

        using var cts = new CancellationTokenSource(ProbeTimeoutMs);
        var probes = targets.Select(addr => ProbeOneAsync(addr, cts.Token)).ToList();
        while (probes.Count > 0)
        {
            var done = await Task.WhenAny(probes).ConfigureAwait(false);
            probes.Remove(done);
            if (done.Result)
            {
                cts.Cancel(); // one live IP is enough; stop the remaining probes
                return true;
            }
        }

        return false; // nothing answered: the set is dead from this exit
    }

    private static async Task<bool> ProbeOneAsync(IPAddress addr, CancellationToken ct)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(new IPEndPoint(addr, ProbePort), ct).ConfigureAwait(false);
            return true; // handshake completed: reachable
        }
        catch (SocketException se) when (se.SocketErrorCode == SocketError.ConnectionRefused)
        {
            return true; // RST: the host answered, the path is alive
        }
        catch
        {
            return false; // timeout / unreachable / cancelled: not reachable via this probe
        }
    }

    // True when the fresh v4 answer is the SAME set (order-independent) as the domain's current v4 IPs. Used
    // to tell a transient probe miss (resolver re-confirms these IPs) from a dead edge (resolver moved it), so
    // a congestion blip doesn't evict a still-advertised address.
    private static bool SameV4Set(IReadOnlyList<string> current, IReadOnlyList<string> fresh)
    {
        var curV4 = new HashSet<string>(current.Where(ip => !ip.Contains(':')), StringComparer.OrdinalIgnoreCase);
        var freshV4 = fresh.Where(ip => !ip.Contains(':')).ToList();
        if (freshV4.Count != curV4.Count)
        {
            return false;
        }

        foreach (var ip in freshV4)
        {
            if (!curV4.Contains(ip))
            {
                return false;
            }
        }

        return true;
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

    private static async Task<byte[]> ForwardAsync(byte[] query, IPAddress upstream, IPAddress? secondary = null, CancellationToken ct = default)
    {
        var upstreams = secondary is null || secondary.Equals(upstream)
            ? new[] { upstream }
            : new[] { upstream, secondary };
        var deadlineMs = Environment.TickCount64 + UpstreamTimeoutMs;
        SocketException? last = null;
        var idx = 0;
        var missesOnCurrent = 0;
        var firstAttempt = true;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var remaining = (int)(deadlineMs - Environment.TickCount64);
            if (remaining <= 0)
            {
                throw last ?? new SocketException((int)SocketError.TimedOut);
            }

            // Retransmit a dropped query after a short wait rather than stalling the whole budget on one lost
            // packet; a fresh socket per attempt lets us fail over between resolvers. The receive is awaited,
            // so a slow/lossy resolver parks a Task, not a pool thread.
            var attemptMs = Math.Min(remaining, UpstreamRetransmitMs);
            var attemptStart = Environment.TickCount64;
            try
            {
                using var client = new UdpClient();
                client.Connect(new IPEndPoint(upstreams[idx], 53));
                client.Send(query, query.Length);
                if (firstAttempt)
                {
                    // One redundant copy up front: the tunnel's lossy underlay drops ~1-in-8 datagrams, so a
                    // second query makes the resolver almost certainly see it within one RTT instead of
                    // waiting out a full retransmit window. The reply leg is covered by the short retransmit.
                    client.Send(query, query.Length);
                }

                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attemptCts.CancelAfter(attemptMs);
                var result = await client.ReceiveAsync(attemptCts.Token).ConfigureAwait(false);
                return result.Buffer;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (
                ex is OperationCanceledException
                || (ex is SocketException se
                    && se.SocketErrorCode is SocketError.TimedOut
                    or SocketError.HostUnreachable
                    or SocketError.NetworkUnreachable
                    or SocketError.ConnectionReset
                    or SocketError.OperationAborted))
            {
                last = ex as SocketException ?? new SocketException((int)SocketError.TimedOut);
                firstAttempt = false;
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
                    await Task.Delay(pause, ct).ConfigureAwait(false);
                }
            }
        }
    }

    // Races the query across every LAN resolver and returns the first answer that carries address records, so
    // a censoring provider's NXDOMAIN is passed over when another provider has the name. Falls back to the
    // first record-less response (a genuine NXDOMAIN/NODATA still returns), or the last error if none answer.
    private static async Task<byte[]> ForwardRacedAsync(byte[] query, IReadOnlyList<IPAddress> pool)
    {
        if (pool.Count <= 1)
        {
            return await ForwardAsync(query, pool[0]).ConfigureAwait(false);
        }

        using var cts = new CancellationTokenSource();
        var pending = pool.Select(ip => ForwardAsync(query, ip, secondary: null, ct: cts.Token)).ToList();
        byte[]? fallback = null;
        Exception? lastError = null;
        while (pending.Count > 0)
        {
            var done = await Task.WhenAny(pending).ConfigureAwait(false);
            pending.Remove(done);
            if (done.Status == TaskStatus.RanToCompletion)
            {
                var resp = done.Result;
                if (DnsMessage.Addresses(resp).Count > 0)
                {
                    cts.Cancel();
                    return resp;
                }

                fallback ??= resp;
            }
            else if (done.Exception is not null)
            {
                lastError = done.Exception.InnerException ?? done.Exception;
            }
        }

        cts.Cancel();
        return fallback ?? throw lastError ?? new SocketException((int)SocketError.TimedOut);
    }

    // Runs the upstream query once per in-flight (name,type); only the leader writes the routing-log line.
    private Task<CoalescedResult> ForwardCoalescedAsync(string? name, int type, byte[] query, IPAddress upstream, IPAddress? secondary = null)
    {
        return CoalesceAsync(name, type, () => ForwardAsync(query, upstream, secondary));
    }

    // Coalesced LAN-pool race: one racing forward per in-flight (name,type).
    private Task<CoalescedResult> ForwardCoalescedRacedAsync(string? name, int type, byte[] query)
    {
        return CoalesceAsync(name, type, () => ForwardRacedAsync(query, _lanPool));
    }

    private async Task<CoalescedResult> CoalesceAsync(string? name, int type, Func<Task<byte[]>> forward)
    {
        if (name is null)
        {
            try
            {
                return new CoalescedResult(await forward().ConfigureAwait(false), Leader: true, Error: null);
            }
            catch (Exception ex)
            {
                return new CoalescedResult(Response: null, Leader: true, ex);
            }
        }

        var key = CacheKey(name, type);
        // GetOrAdd(key, value): the caller whose instance is stored is the leader. The Lazy holds the shared
        // forward Task so concurrent identical misses await one upstream query.
        var mine = new Lazy<Task<byte[]>>(forward, LazyThreadSafetyMode.ExecutionAndPublication);
        var lazy = _inflight.GetOrAdd(key, mine);
        var leader = ReferenceEquals(lazy, mine);
        try
        {
            return new CoalescedResult(await lazy.Value.ConfigureAwait(false), leader, Error: null);
        }
        catch (Exception ex)
        {
            return new CoalescedResult(Response: null, leader, ex);
        }
        finally
        {
            // Remove only our own entry so a racing newcomer's fresh Lazy is left intact.
            _inflight.TryRemove(new KeyValuePair<string, Lazy<Task<byte[]>>>(key, lazy));
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
                    await CollectAddressesAsync(host, 1, ips);
                    if (!_stripV6)
                    {
                        await CollectAddressesAsync(host, 28, ips);
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

    private async Task CollectAddressesAsync(string host, int type, List<IPAddress> ips)
    {
        var response = await ForwardAsync(DnsMessage.BuildQuery(host, type), _tunnelUpstream, _tunnelUpstreamSecondary).ConfigureAwait(false);
        foreach (var ip in DnsMessage.Addresses(response))
        {
            ips.Add(ip);
        }
    }
}
