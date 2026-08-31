using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using AmneziaGeo.Routing;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Loopback DNS proxy that forwards queries through the tunnel and feeds resolved tunneled domains
/// to the domain tracker.
/// </summary>
internal sealed class DnsProxy
{
    // Whole budget for one upstream query. The retransmits and the failover to the secondary resolver all fit
    // inside it, and past it the name is resolved locally instead - waiting longer only holds the client still.
    private const int UpstreamTimeoutMs = 2000;
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
    // Serve-stand-in: TTL on an answer given to a client of the access point. Short so the client asks again
    // while it keeps using the name, which renews the address it holds; the address itself lives far longer, so a
    // connection opened late still finds the name it stands for.
    private const int StandInTtlSeconds = 30;
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

    // Loopback aliases the proxy may listen on: 127.0.0.1:53 is taken by another resolver, or by another tunnel
    // of the set, so every tunnel a machine keeps up gets one of its own.
    private static readonly IPAddress[] V4Candidates = [.. Enumerable.Range(1, 8).Select(last => IPAddress.Parse($"127.0.0.{last}"))];

    /// <summary>
    /// Name the liveness probe asks for. Answered here without an upstream, so a slow resolver never reads as a
    /// proxy that stopped serving.
    /// </summary>
    public const string HealthName = "health.ageo.arpa";

    /// <summary>
    /// Address the health name answers with. It names this proxy: another resolver in its place returns NXDOMAIN
    /// for a name that exists nowhere, so an answer is proof the query reached here.
    /// </summary>
    public const string HealthAddress = "127.0.0.53";

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
    // Hard caps so a long session cannot grow these per-domain sets unboundedly; overflow clears wholesale (a
    // miss just re-forwards or re-runs the matcher). Otherwise cleared only on matcher rebuild / reconnect.
    private const int MaxCacheEntries = 8192;
    private const int MaxBypassEntries = 8192;
    // Volatile: read on the hot query path, replaced on the poll thread; the matcher is immutable.
    private volatile IReadOnlyList<GeoDomain> _domains;
    private volatile DomainMatcher _matcher;
    // Names another tunnel of the set carries: which one owns a name, and how it is asked to carry it. Both
    // stay empty on a machine that keeps one tunnel.
    private volatile Func<string, string?>? _lentOwner;
    private volatile Func<string, string, Task<IReadOnlyList<string>>>? _lentCarry;
    // Block bucket: names refused with NXDOMAIN before any tunnel/bypass decision; never tunneled or resolved.
    private volatile IReadOnlyList<GeoDomain> _blockDomains;
    private volatile DomainMatcher _blockMatcher;
    private volatile bool _hasBlockDomains;
    // Direct bucket on its own, next to the local suffixes it is merged into for resolution: only these names
    // settle a Direct verdict for their addresses, a suffix of your own network settles nothing.
    private volatile IReadOnlyList<GeoDomain> _directDomains;
    private volatile DomainMatcher _directMatcher;
    private volatile bool _hasDirectDomains;
    private readonly IPAddress _tunnelUpstream;
    private readonly IPAddress? _tunnelUpstreamSecondary;
    private readonly IPAddress _localUpstream;
    private readonly IPAddress? _lanUpstream;
    // All LAN resolvers; a multi-provider box races them and takes the first answer with records so a
    // censoring provider's NXDOMAIN is passed over.
    private readonly IReadOnlyList<IPAddress> _lanPool;
    // Non-geo names resolve on the LAN (raceable) in split mode; offshore through the tunnel in full mode.
    private readonly bool _localIsLan;
    // Suffixes the session was built with, kept apart from the Direct bucket so a list edit rebuilds only it.
    private readonly IReadOnlyList<string> _staticLocalDomains;
    private volatile IReadOnlyList<string> _localDomains;
    private readonly DomainTracker? _tracker;
    private readonly ILogger<DnsProxy> _logger;
    private readonly bool _stripV6;
    // Names queried by a matched app resolve through the tunnel and route their answer, even with no geo rule.
    private readonly AppDnsTracker? _appDns;
    // Direct-verdict addresses get their host route here, before the answer reaches the client.
    private readonly RoutingCache? _routing;
    // Queries the tunnel resolver failed to answer and the local one took over, since it last answered. Kept so
    // the takeover and the recovery are each said once instead of per query.
    private int _rescued;
    // Names the clients of the access point were given a stand-in address for; empty while no point stands.
    private HotspotNames? _clientNames;
    // Socket on the address the access point hands its clients. A query arriving there is a client's.
    private UdpClient? _clientServer;

    /// <summary>
    /// ctor
    /// </summary>
    public DnsProxy(IReadOnlyList<GeoDomain> domains, IReadOnlyList<GeoDomain> blockDomains, IPAddress tunnelUpstream, IPAddress localUpstream, IPAddress? lanUpstream, IReadOnlyList<IPAddress> lanPool, bool localIsLan, IReadOnlyList<string> localDomains, IReadOnlyList<GeoDomain> directDomains, DomainTracker? tracker, ILogger<DnsProxy> logger, bool stripV6, IPAddress? tunnelSecondary = null, AppDnsTracker? appDns = null, RoutingCache? routing = null, bool listen = true)
    {
        _appDns = appDns;
        _routing = routing;
        _domains = domains;
        _matcher = new DomainMatcher(domains);
        _blockDomains = blockDomains;
        _blockMatcher = new DomainMatcher(blockDomains);
        _hasBlockDomains = blockDomains.Count > 0;
        _directDomains = directDomains;
        _directMatcher = new DomainMatcher(directDomains);
        _hasDirectDomains = directDomains.Count > 0;
        _tunnelUpstream = tunnelUpstream;
        _tunnelUpstreamSecondary = tunnelSecondary;
        _localUpstream = localUpstream;
        _lanUpstream = lanUpstream;
        _lanPool = lanPool;
        _localIsLan = localIsLan;
        _staticLocalDomains = Normalize(localDomains);
        _localDomains = WithDirect(_staticLocalDomains, directDomains);
        _tracker = tracker;
        _logger = logger;
        _stripV6 = stripV6;

        if (!listen)
        {
            // The machine asks one tunnel of the set, and this is not it: names arrive over the pipe of the one
            // that holds them, so no loopback is taken here.
            _logger.LogInformation("names are looked up here only for the tunnel holding this machine's lookups: {Domains} domain rule(s), tunnel resolver {TunnelUp}",
                _domains.Count, _tunnelUpstream);
            return;
        }

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

        _logger.LogInformation("DNS is now handled here: {Domains} domain rule(s); tunnel resolver {TunnelUp} (backup {TunnelUp2}), direct resolver {LocalUp}, LAN resolver {LanUp} (pool {LanPool}), {LocalDomains} local suffix(es); listening on {V4} and {V6}, IPv6 answers suppressed: {StripV6}",
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
            var thread = new Thread(() => ServeOne(server, clients: false))
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

    /// <summary>
    /// Serves the clients of the access point on the address it hands them. Windows gives a datagram to the
    /// closest bind, so queries land here even while the sharing service holds every address on port 53.
    /// </summary>
    public bool ServeClients(IPAddress address, HotspotNames names)
    {
        if (_clientServer is not null)
        {
            return true;
        }

        try
        {
            var server = new UdpClient(new IPEndPoint(address, 53));
            server.Client.IOControl(SioUdpConnReset, new byte[4], null);
            _clientNames = names;
            _clientServer = server;
            var thread = new Thread(() => ServeOne(server, clients: true))
            {
                IsBackground = true,
            };
            thread.Start();
            _logger.LogInformation("the clients of the access point now ask this machine for names, on {Address}:53", address);
            return true;
        }
        catch (SocketException ex)
        {
            _logger.LogWarning(ex, "names for the clients of the access point could not be taken over on {Address}:53; what they open keeps leaving past the rules of this machine", address);
            _clientNames = null;
            return false;
        }
    }

    /// <summary>
    /// Stops serving the clients of the access point and drops every stand-in address handed out.
    /// </summary>
    public void StopServingClients()
    {
        var server = Interlocked.Exchange(ref _clientServer, null);
        if (server is null)
        {
            return;
        }

        _clientNames?.Clear();
        _clientNames = null;
        server.Dispose();
    }

    /// <summary>
    /// Points the names another tunnel of the set carries at it: the first delegate names that tunnel, the
    /// second has it look the name up and put the addresses on its own path.
    /// </summary>
    public void SetLentNames(Func<string, string?> owner, Func<string, string, Task<IReadOnlyList<string>>> carry)
    {
        _lentOwner = owner;
        _lentCarry = carry;
        ClearCache();
    }

    /// <summary>
    /// Looks a name up for the tunnel holding this machine's lookups and puts its addresses on this one.
    /// Empty when no rule here names it, or when nothing answered.
    /// </summary>
    public async Task<IReadOnlyList<string>> CarryAsync(string name)
    {
        if (!_matcher.IsTunneled(name))
        {
            _logger.LogDebug("{Name}: no rule of this tunnel names it, so it is not carried here", name);
            return [];
        }

        var ips = new List<IPAddress>();
        await CollectAddressesAsync(name, TypeA, ips).ConfigureAwait(false);
        if (ips.Count == 0)
        {
            _logger.LogWarning("{Name}: the resolver in this tunnel did not answer, so what a rule sent here leaves directly until the next query", name);
            return [];
        }

        var addresses = ips.Select(ip => ip.ToString()).ToList();
        _tracker?.Add(name, addresses);
        _logger.LogInformation("{Name}: looked up for the tunnel holding this machine's names; its {Count} address(es) now go through this one", name, addresses.Count);
        return addresses;
    }

    /// <summary>
    /// Drops any cached local answer and negative-cache entry for a name, so a name just marked app-tunneled is
    /// re-resolved through the tunnel on its next query instead of serving a pre-mark local (poisoned) result.
    /// </summary>
    public void InvalidateName(string name)
    {
        var key = name.TrimEnd('.').ToLowerInvariant();
        _cache.TryRemove(CacheKey(key, TypeA), out _);
        _cache.TryRemove(CacheKey(key, TypeAaaa), out _);
        _cache.TryRemove(CacheKey(key, TypeHttps), out _);
        _bypass.TryRemove(key, out _);
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
        if (_bypass.Count >= MaxBypassEntries)
        {
            _bypass.Clear();
        }

        _bypass[key] = Environment.TickCount64 + (BypassTtlSeconds * 1000L);
    }

    /// <summary>
    /// Rebuilds the Direct and Block buckets live from an edited list; true when either moved, so the caller can
    /// flush the OS resolver cache and let a name decided under the old rules be asked again.
    /// </summary>
    public bool UpdateBuckets(IReadOnlyList<GeoDomain> blockDomains, IReadOnlyList<GeoDomain> directDomains)
    {
        var locals = WithDirect(_staticLocalDomains, directDomains);
        if (_blockDomains.SequenceEqual(blockDomains) && _localDomains.SequenceEqual(locals, StringComparer.Ordinal))
        {
            return false;
        }

        _blockDomains = blockDomains;
        _blockMatcher = new DomainMatcher(blockDomains);
        _hasBlockDomains = blockDomains.Count > 0;
        _directDomains = directDomains;
        _directMatcher = new DomainMatcher(directDomains);
        _hasDirectDomains = directDomains.Count > 0;
        _localDomains = locals;

        // Drop cached answers and the negative cache: a name refused or kept local now may hold a verdict from
        // before the edit.
        _cache.Clear();
        _bypass.Clear();

        _logger.LogInformation("direct and blocked names reloaded without reconnecting: {Local} local suffix(es), {Block} blocked rule(s) now in effect", locals.Count, blockDomains.Count);
        return true;
    }

    /// <summary>
    /// What the name rules settle for a name. Block wins over Direct, Direct over the tunnel - the same order the
    /// ranges are read in, so a name and an address covering each other never give two answers.
    /// </summary>
    public RouteVerdict NameVerdict(string name)
    {
        if (_hasBlockDomains && _blockMatcher.IsTunneled(name))
        {
            return RouteVerdict.Block;
        }

        if (_hasDirectDomains && _directMatcher.IsTunneled(name))
        {
            return RouteVerdict.Direct;
        }

        return _matcher.IsTunneled(name) ? RouteVerdict.Proxy : RouteVerdict.None;
    }

    // Suffixes as the local check wants them: trimmed, dotless at the ends, lower case.
    private static IReadOnlyList<string> Normalize(IEnumerable<string> domains) =>
        [.. domains.Select(d => d.Trim().Trim('.').ToLowerInvariant()).Where(d => d.Length > 0)];

    // The session's own suffixes plus the Direct bucket, whose names resolve locally and stay off the tunnel.
    private static IReadOnlyList<string> WithDirect(IReadOnlyList<string> statics, IReadOnlyList<GeoDomain> direct)
    {
        var merged = new List<string>(statics);
        foreach (var name in Normalize(direct.Select(d => d.Value)))
        {
            if (!merged.Contains(name))
            {
                merged.Add(name);
            }
        }

        return merged;
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

        _logger.LogInformation("domain rules reloaded without reconnecting: {Count} rule(s) now in effect", domains.Count);
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
            _logger.LogInformation("{Count} domain(s) left the rules and were taken out of the tunnel", removed);
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

        _logger.LogInformation("resolving {Count} newly added domain(s) now, so they work without a first-use delay", added.Count);
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
            _logger.LogDebug(ex, "the newly added domains could not be resolved up front; they resolve when first used");
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
            _logger.LogWarning(ex, "DNS on {Address}:53 could not be taken over - another program holds it; names are resolved by the system instead, so rules by domain will not apply", address);
            return false;
        }
    }

    private void ServeOne(UdpClient server, bool clients)
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
                    _ = HandleAsync(server, query, client, clients);
                }
            }
        }
        catch (Exception ex)
        {
            if (clients)
            {
                _logger.LogDebug(ex, "names are no longer served to the clients of the access point");
                return;
            }

            _logger.LogError(ex, "DNS handling stopped on {Address}; names fall back to the system resolver and rules by domain stop applying", anyEndpoint);
        }
    }

    private async Task HandleAsync(UdpClient server, byte[] query, IPEndPoint client, bool isClient)
    {
        try
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var name = DnsMessage.QuestionName(query);
            var type = DnsMessage.QuestionType(query);

            // Liveness probe: answered on the spot, ahead of every rule, so the answer proves only that queries
            // still reach this socket and are served.
            if (name is not null && string.Equals(name.TrimEnd('.'), HealthName, StringComparison.OrdinalIgnoreCase))
            {
                var alive = type == TypeA ? DnsMessage.BuildAAnswer(query, [HealthAddress], 0) : DnsMessage.BuildNoData(query);
                lock (server)
                {
                    server.Send(alive, alive.Length, client);
                }

                return;
            }

            // Block bucket wins over everything: a matched name is refused (NXDOMAIN) before any tunnel/bypass
            // decision, so it never resolves and its connection is dropped.
            if (_hasBlockDomains && name is not null && _blockMatcher.IsTunneled(name))
            {
                var blocked = DnsMessage.BuildNxDomain(query);
                lock (server)
                {
                    server.Send(blocked, blocked.Length, client);
                }

                _logger.LogDebug("{Name} {Type}: matches a block rule; the client is told this name does not exist", name, TypeLabel(type));
                if (RouteLog.Enabled)
                {
                    RouteLog.Note($"block {name} {TypeLabel(type)} -> NXDOMAIN");
                }

                return;
            }

            // The clients of the access point take an IPv4 stand-in and nothing else: an IPv6 answer, or one
            // carrying shortcut addresses, would send them out past this machine.
            if (isClient && (type == TypeAaaa || type == TypeHttps))
            {
                var withheld = DnsMessage.BuildNoData(query);
                lock (server)
                {
                    server.Send(withheld, withheld.Length, client);
                }

                return;
            }

            // Local/LAN names resolve via the LAN resolver and stay off the tunnel.
            var isLocal = name is not null && _lanUpstream is not null && IsLocalName(name);

            // Negative cache: a name already proven to be in no geo rule bypasses the matcher and the tunnel.
            var bypassed = name is not null && IsBypassed(name);

            // App-tunnel: a name recently queried by a matched app resolves through the tunnel and routes its
            // answer, even with no geo rule. Its decision comes from DNS-Client ETW, not the geo matcher, so it
            // overrides the bypass negative-cache.
            var appDns = name is not null && !isLocal && _appDns is not null && _appDns.IsTunneled(name);

            // Matched names resolve via the clean tunnel resolver; others use the local resolver.
            var geoMatch = !isLocal && !bypassed && name is not null ? _matcher.Match(name) : null;
            var matched = geoMatch is not null || appDns;

            // A name no rule here matches may still be named by a rule riding another tunnel of the set. This
            // machine looks addresses up in one place, so that tunnel never sees the name: it is handed over.
            var lentTo = !isLocal && !bypassed && !matched && name is not null ? _lentOwner?.Invoke(name) : null;

            // Remember a non-local miss so the matcher isn't re-run for it until the lists change. An app-tunnel
            // name is matched, so it is never bypassed.
            if (name is not null && !isLocal && !bypassed && !matched && lentTo is null)
            {
                MarkBypassed(name);
            }

            // Why this name is treated the way it is, spelled out for the log line below.
            var decision = DecisionLabel(isLocal, appDns, geoMatch);

            byte[] response;
            var fromCache = false;
            // What became of this query, and whether this call is the one that did the work: followers of a
            // coalesced query repeat the leader's outcome and are not worth a line each.
            var outcome = default(string);
            var leader = true;
            if (!isLocal && _stripV6 && type == TypeAaaa)
            {
                // IPv4-only tunnel: NODATA for AAAA so clients use IPv4.
                response = DnsMessage.BuildNoData(query);
                outcome = "answered without an IPv6 address, this tunnel carries IPv4 only, so the client will use IPv4 instead";
            }
            else if (!isLocal && type == TypeHttps)
            {
                // Deny HTTPS/SVCB records: their hint addresses bypass the tunnel.
                response = DnsMessage.BuildNoData(query);
                outcome = "answer withheld, this record carries shortcut addresses that would skip the tunnel, so the client asks again the ordinary way";
            }
            else if (TryGetCached(name, type, query, out var cached))
            {
                response = cached;
                fromCache = true;
                outcome = "answered from cache";
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
                outcome = $"answered from its {known.Count} known address(es), already in the tunnel — checking in the background that they still respond";
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
                outcome = $"restored {hydrated.Count} address(es) saved from an earlier session and put them back in the tunnel";
            }
            else if (lentTo is not null && type == TypeA
                     && await AskOwnerAsync(lentTo, name!).ConfigureAwait(false) is { Count: > 0 } lent)
            {
                // The owner looked it up through its own tunnel and put the addresses there; this side only
                // answers the client.
                response = DnsMessage.BuildAAnswer(query, lent, ServeKnownTtlSeconds);
                StoreInCache(name, type, response);
                outcome = $"looked up on {lentTo}, the tunnel its rule names, and its {lent.Count} address(es) go through that one";
            }
            else if (lentTo is not null && type != TypeA)
            {
                // The tunnel carrying it took IPv4 addresses; an answer of another kind would leave past it.
                response = DnsMessage.BuildNoData(query);
                outcome = $"answered without an address of this kind, {lentTo} carries this name over IPv4";
            }
            else
            {
                var upstream = isLocal ? _lanUpstream! : (matched ? _tunnelUpstream : _localUpstream);
                var secondary = matched ? _tunnelUpstreamSecondary : null;
                // LAN-bound names (local, or non-geo in split) race the whole provider pool.
                var lanRace = _lanPool.Count > 1 && (isLocal || (!matched && _localIsLan));
                var result = lanRace
                    ? await ForwardCoalescedRacedAsync(name, type, query)
                    : await ForwardCoalescedAsync(name, type, query, upstream, secondary);
                leader = result.Leader;
                // The tunnel resolver went silent: ask the local one rather than leave the client without an
                // answer. The addresses still take the tunnel below.
                var rescued = false;
                if (result.Error is not null && matched && !isLocal && !lanRace
                    && await RescueAsync(name, type, query).ConfigureAwait(false) is { } local)
                {
                    result = new CoalescedResult(local, leader, Error: null);
                    rescued = true;
                }

                if (result.Error is not null)
                {
                    // Notes an upstream that did not answer.
                    _logger.LogDebug("{Name} {Type}: {Decision}, asked {Resolver} — no answer ({Reason}); the client is told to try again",
                        name, TypeLabel(type), decision, ResolverLabel(isLocal, matched, lanRace, upstream), result.Error.Message);
                    if (RouteLog.Enabled && name is not null && result.Leader)
                    {
                        RouteLog.Note(FormatRouteQuery(name, type, isLocal, matched, appDns, geoMatch, upstream, started, ips: null, failure: result.Error.Message));
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
                if (matched && !rescued && Interlocked.Exchange(ref _rescued, 0) > 0)
                {
                    _logger.LogInformation("the resolver in the tunnel is answering again, so names are looked up there once more");
                }

                // The app-tunnel mark can land while this local forward was in flight. If the name flipped to
                // app-tunneled, don't serve or cache the local (possibly poisoned) answer: drop it and fail
                // transient so the app's retry resolves through the tunnel instead.
                if (!matched && name is not null && _appDns is not null && _appDns.IsTunneled(name))
                {
                    InvalidateName(name);
                    var servfail = DnsMessage.BuildServFail(query);
                    lock (server)
                    {
                        server.Send(servfail, servfail.Length, client);
                    }

                    return;
                }

                // A rescued answer is served but never cached, so the tunnel resolver is asked again next time.
                if (!rescued)
                {
                    StoreInCache(name, type, shared);
                }

                // Followers share the leader's buffer; answer each client with its own transaction id.
                response = ApplyTransactionId(shared, query);

                var addresses = name is null ? [] : DnsMessage.Addresses(shared).Select(a => a.ToString()).ToList();

                // Feed the app-promotion hint cache from every real resolution (matched or not), so an app CDN
                // domain in no geo rule still populates the reverse map; a promoted name routes its IPs here.
                if (name is not null && _tracker is not null && addresses.Count > 0)
                {
                    _tracker.NoteResolution(name, addresses);
                }

                // Routing-log line for a real resolution, written only by the coalescing leader.
                if (RouteLog.Enabled && name is not null && result.Leader)
                {
                    RouteLog.Note(FormatRouteQuery(name, type, isLocal, matched, appDns, geoMatch, upstream, started, addresses, failure: null));
                }

                outcome = rescued
                    ? $"the resolver in the tunnel did not answer, so your own network's resolver was asked instead: {addresses.Count} address(es) in {ElapsedMs(started)} ms"
                    : $"asked {ResolverLabel(isLocal, matched, lanRace, upstream)}, got {addresses.Count} address(es) in {ElapsedMs(started)} ms";
            }

            // Route a matched domain before answering, or the client's first SYN egresses off-tunnel.
            // Tracking failure is isolated so the answer still goes out; cache hits were already tracked.
            if (matched && !fromCache)
            {
                try
                {
                    Track(name!, response, appDns);
                }
                catch (Exception ex)
                {
                    // Route installation failed; the matched domain won't route through the tunnel.
                    _logger.LogWarning(ex, "{Name}: its addresses could not be put in the tunnel; this traffic leaves directly until the next query", name);
                    if (RouteLog.Enabled)
                    {
                        RouteLog.Note($"route FAILED for {name}: {ex.Message}");
                    }
                }
            }

            // Install Direct host routes before the answer leaves, so the client's first packet already egresses the
            // physical path. Runs for cached answers too: a route reclaimed while idle is restored on the next query.
            if (_routing is not null)
            {
                try
                {
                    // A name in the Direct or Block bucket decides for the addresses it answers with, so a range
                    // covering one of them cannot pull it back into the tunnel.
                    var byName = name is null ? RouteVerdict.None : NameVerdict(name);
                    foreach (var address in DnsMessage.Addresses(response))
                    {
                        if (byName is RouteVerdict.Direct or RouteVerdict.Block)
                        {
                            _routing.Note(address, byName);
                        }
                        else
                        {
                            _routing.Note(address);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "{Name}: its addresses could not be classified against the routing rules", name);
                }
            }

            // One line per query: what was asked, where it goes, what was done. Followers of a coalesced query
            // repeat the leader's work, so they stay silent.
            if (name is not null && leader && outcome is not null)
            {
                _logger.Log(matched ? LogLevel.Debug : LogLevel.Trace, "{Name} {Type}: {Decision}; {Outcome}",
                    name, TypeLabel(type), decision, outcome);
            }

            // A client of the access point is answered with a stand-in address, so what it opens is terminated
            // here and opened again as a socket of this machine - carried by the same rules, and leaving with
            // the address of the path it takes instead of the one the sharing NAT stamps on everything.
            var names = _clientNames;
            if (isClient && names is not null && name is not null && type == TypeA && DnsMessage.Addresses(response).Count > 0)
            {
                var stand = names.Take(name);
                response = DnsMessage.BuildAAnswer(query, [stand.ToString()], StandInTtlSeconds);
                _logger.LogDebug("{Name}: a client of the access point is answered with {Stand}, so this machine carries what it opens", name, stand);
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
                _logger.LogDebug("{Name}: its addresses could not be rechecked, the tunnel resolver is unreachable ({Reason}); keeping the ones already in use", name, ex.Message);
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
                _logger.LogDebug("{Name}: its addresses went quiet, but the resolver returns the same ones; keeping them", name);
                return;
            }

            // Genuinely dead: the resolver moved the domain to a different set. Replace evicts the dropped/dead
            // IPs and installs the fresh ones - this is the save of the new value.
            tracker.Replace(name, fresh);

            // Drop the short synthetic serve-known answer so the next client query serves the fresh set at
            // once instead of the now-dead IP for up to ServeKnownTtlSeconds.
            _cache.TryRemove(CacheKey(name, TypeA), out _);

            _logger.LogInformation("{Name}: its addresses stopped responding, so it was resolved again to {Ips}", name, string.Join(", ", fresh));
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
        if (_cache.Count >= MaxCacheEntries)
        {
            _cache.Clear();
        }

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
                    or SocketError.NoBufferSpaceAvailable
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

    // Resolves a tunneled name on the LAN resolver once the tunnel one has gone silent, so a resolver that died -
    // or a route to it that went away - costs one slow query instead of leaving the client with no answer at all.
    // Null when there is no LAN resolver or it does not answer either.
    private async Task<byte[]?> RescueAsync(string? name, int type, byte[] query)
    {
        IReadOnlyList<IPAddress> pool = _lanPool;
        if (pool.Count == 0 && _lanUpstream is not null)
        {
            pool = [_lanUpstream];
        }

        if (pool.Count == 0)
        {
            return null;
        }

        if (Interlocked.Increment(ref _rescued) == 1)
        {
            _logger.LogWarning("the resolver in the tunnel stopped answering; names are looked up on your own network's resolver until it responds again, so browsing keeps working");
        }

        var result = pool.Count > 1
            ? await CoalesceAsync(name, type, () => ForwardRacedAsync(query, pool)).ConfigureAwait(false)
            : await CoalesceAsync(name, type, () => ForwardAsync(query, pool[0])).ConfigureAwait(false);
        if (result.Error is not null)
        {
            _logger.LogDebug("{Name}: your own network's resolver did not answer either ({Reason})", name, result.Error.Message);
            return null;
        }

        return result.Response;
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
    private static string FormatRouteQuery(string name, int type, bool isLocal, bool matched, bool appDns, DomainMatcher.GeoMatch? geoMatch, IPAddress upstream, long startedTimestamp, IReadOnlyList<string>? ips, string? failure)
    {
        var ms = ElapsedMs(startedTimestamp);
        var decision = isLocal ? "LAN" : matched ? "TUNNEL" : "LOCAL";
        var rule = matched && geoMatch is { } gm ? "  rule=" + RuleLabel(gm) : matched && appDns ? "  rule=app" : string.Empty;
        if (failure is not null)
        {
            return $"{name} {TypeLabel(type)} -> {decision}  FAILED  up={upstream}  {ms}ms{rule}  {failure}";
        }

        var ipText = ips is null || ips.Count == 0
            ? "-"
            : ips.Count <= 6 ? string.Join(",", ips) : string.Join(",", ips.Take(6)) + $" +{ips.Count - 6} more";
        return $"{name} {TypeLabel(type)} -> {decision}  ip={ipText}  up={upstream}  {ms}ms{rule}";
    }

    // Milliseconds since a Stopwatch timestamp.
    private static long ElapsedMs(long from) => (long)System.Diagnostics.Stopwatch.GetElapsedTime(from).TotalMilliseconds;

    // DNS record type -> label that says what the client asked for; unknown types fall back to "typeNN".
    private static string TypeLabel(int type) => type switch
    {
        1 => "A/IPv4",
        28 => "AAAA/IPv6",
        65 => "HTTPS/SVCB",
        5 => "CNAME",
        12 => "PTR",
        15 => "MX",
        16 => "TXT",
        33 => "SRV",
        2 => "NS",
        6 => "SOA",
        _ => "type" + type.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    // Why this name is sent where it is sent, in words.
    private static string DecisionLabel(bool isLocal, bool appDns, DomainMatcher.GeoMatch? geoMatch)
    {
        if (isLocal)
        {
            return "a name of your own network";
        }

        if (geoMatch is { } match)
        {
            return $"matches the tunnel rule {RuleLabel(match)}";
        }

        return appDns ? "belongs to an app you route through the tunnel" : "matches no rule";
    }

    // Which resolver the query was sent to, and by which path.
    private static string ResolverLabel(bool isLocal, bool matched, bool raced, IPAddress upstream)
    {
        if (raced)
        {
            return "every resolver of your provider at once";
        }

        var kind = isLocal ? "your network's resolver" : matched ? "the resolver inside the tunnel" : "your provider's resolver";
        return $"{kind} {upstream}";
    }

    // The matched geo rule as "<kind>:<value>" (e.g. "domain:openai.com").
    private static string RuleLabel(DomainMatcher.GeoMatch match) => match.Kind switch
    {
        GeoDomainKind.Full => "full:" + match.Value,
        GeoDomainKind.Domain => "domain:" + match.Value,
        GeoDomainKind.Plain => "plain:" + match.Value,
        GeoDomainKind.Regex => "regex:" + match.Value,
        _ => match.Value,
    };

    private void Track(string name, byte[] response, bool appDns)
    {
        var ips = new List<string>();
        foreach (var ip in DnsMessage.Addresses(response))
        {
            ips.Add(ip.ToString());
        }

        // Re-check membership at Add time: _matcher may have swapped (a list edit) between the match that
        // routed this query here and now - do not (re-)route a domain that just left the routing lists. An
        // app-tunnel name has no geo rule, so it routes on the app decision alone.
        if (ips.Count > 0 && (appDns || _matcher.IsTunneled(name)))
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
                _logger.LogWarning("{Host}: could not be resolved through the tunnel; it will be tried again when something asks for it", host);
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

    // Hands one name to the tunnel that owns it and waits for the addresses it put on itself.
    private async Task<IReadOnlyList<string>> AskOwnerAsync(string owner, string name)
    {
        var carry = _lentCarry;
        if (carry is null)
        {
            return [];
        }

        try
        {
            return await carry(owner, name).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "{Name}: {Owner} did not answer, so it stays off that tunnel until the next query", name, owner);
            return [];
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
