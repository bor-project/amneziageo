using System.Net;
using System.Net.Sockets;
using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using AmneziaGeo.Routing;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Resolves tunneled domains to IPs on demand and keeps a live, in-memory set of RULE-BACKED resolutions
/// (their /32 routes + allowed-ips). Resolutions are persisted to the domain_ips table and restored LAZILY:
/// nothing is pre-resolved or bulk warm-started, and a matched domain is hydrated from the DB only when it is
/// actually queried (<see cref="TryHydrateFromCacheAsync"/>), so memory holds just what this session used. A
/// name that is NOT in any rule never lands here (it bypasses the tunnel, negatively cached by the DNS proxy).
/// An actively-used domain self-heals via <see cref="Replace"/> (reachability-gated re-resolve + evict) and
/// every change is written back, so a stale/dead CDN IP is dropped rather than accumulated.
/// </summary>
internal sealed class DomainTracker(
    IStateStore store,
    RouteManager routes,
    UapiClient uapi,
    ILogger<DomainTracker> logger,
    string tunnelName,
    string peerPublicKey,
    IReadOnlyList<string> staticRoutes,
    IReadOnlyList<string> listRoutes,
    int routeTtlSeconds,
    bool stripV6,
    bool lazyRanges,
    RoutingCache? routing = null,
    SynSentReset? synReset = null)
{
    private readonly object _lock = new();
    private readonly Dictionary<string, HashSet<string>> _current = [];
    // App-discovered remote IPs, unioned into allowed-ips so the watcher and DNS path share one authority. Each
    // carries its last contact and ages on the same idle window as a name: an address nobody talks to is reclaimed
    // whatever put it here.
    private readonly Dictionary<string, long> _appIps = [];

    // App-promotion hint cache (non-authoritative): learned name->IPs and its reverse index, plus the set of
    // app-promoted domains. Feeds route-before-answer for a matched app's repeat domains; stale entries are
    // harmless (at worst one dead /32), so these are NOT mirrored on Add/Replace/Remove eviction.
    private readonly Dictionary<string, HashSet<string>> _nameToIps = [];
    private readonly Dictionary<string, HashSet<string>> _ipToNames = [];
    private readonly HashSet<string> _promotedApps = new(StringComparer.Ordinal);
    private const int MaxLearnedIps = 8192;

    // All static geoip CIDRs advertised in allowed-ips: list ranges + connect infrastructure (tunnel-DNS /32s).
    private readonly HashSet<string> _staticRoutes = new(staticRoutes, StringComparer.Ordinal);

    // The reconcilable subset: ranges that came from the routing list. Only these are removed when a list drops
    // them - infrastructure routes (in _staticRoutes but not here, e.g. the tunnel resolver /32s) are never touched.
    private readonly HashSet<string> _listRoutes = new(stripV6 ? listRoutes.Where(c => !c.Contains(':')) : listRoutes, StringComparer.Ordinal);

    // Last time each tracked domain was resolved or served; drives eviction on the same idle window the address
    // cache uses, so a name nobody visits stops holding routes.
    private readonly Dictionary<string, long> _touched = new(StringComparer.Ordinal);
    private long _idleTtlMs = Math.Max(routeTtlSeconds, 0) * 1000L;
    // Sweep cadence follows the window: a short lifetime is checked more often so it is actually honoured.
    private int _evictIntervalMs = EvictInterval(routeTtlSeconds);

    private static int EvictInterval(int seconds)
    {
        return (int)Math.Clamp(Math.Max(seconds, 0) * 1000L / 5, 5_000, 30_000);
    }

    /// <summary>
    /// Applies an idle window to the domains already tracked; the next sweep drops whatever it now covers.
    /// </summary>
    public void SetTtl(int seconds)
    {
        Volatile.Write(ref _idleTtlMs, Math.Max(seconds, 0) * 1000L);
        Volatile.Write(ref _evictIntervalMs, EvictInterval(seconds));
    }

    // Baseline for the poll signal: list materialization generation.
    private long? _knownGeneration;

    // Live geo-domain sink; rebuilt on materialization generation change so a source refresh takes effect without reconnect.
    private volatile Action<IReadOnlyList<GeoDomain>, CancellationToken>? _onGeoDomainsChanged;

    private uint? _interfaceIndex;

    // The routing list currently projected onto this tunnel; tags persisted rows so a list's cached resolutions
    // are cleaned when the list is removed (domain_ips.list_id). Read/written under _lock. 0 = none/unknown.
    private long _activeListId;

    // Serialises this tunnel's resolution writes so a later change never lands in the DB before an earlier one.
    private readonly object _persistLock = new();
    private Task _persistTail = Task.CompletedTask;

    // Kept only so any awaiter of WarmStartCompleted (e.g. the retained DnsProxy.SeedRoutesAsync) never hangs;
    // this build has no DB warm start - the in-memory cache is populated purely on demand.
    private readonly TaskCompletionSource _warmStart = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Completes immediately in this build (no DB warm start); retained for callers that still await it.
    /// </summary>
    public Task WarmStartCompleted => _warmStart.Task;

    /// <summary>
    /// Attaches the live geo-domain sink; a generation change rebuilds the proxy matcher without reconnect.
    /// </summary>
    public void SetGeoDomainSink(Action<IReadOnlyList<GeoDomain>, CancellationToken> sink)
    {
        _onGeoDomainsChanged = sink;
    }

    /// <summary>
    /// Whether any tracked domain still holds this address.
    /// </summary>
    public bool Holds(IPAddress address)
    {
        var text = address.ToString();
        lock (_lock)
        {
            foreach (var pair in _current)
            {
                if (pair.Value.Contains(text))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Domains tracked right now, each with the addresses whose routes are installed for it.
    /// </summary>
    public IReadOnlyList<(string Domain, IReadOnlyList<string> Ips, int IdleSeconds, int TtlSeconds)> Snapshot()
    {
        var now = Environment.TickCount64;
        var ttl = (int)(Volatile.Read(ref _idleTtlMs) / 1000);
        lock (_lock)
        {
            var result = new List<(string, IReadOnlyList<string>, int, int)>(_current.Count);
            foreach (var pair in _current)
            {
                var touched = LastContactLocked(pair.Key, _touched.TryGetValue(pair.Key, out var last) ? last : now);
                var idle = (int)Math.Clamp((now - touched) / 1000, 0, ttl);
                result.Add((pair.Key, [.. pair.Value], idle, ttl));
            }

            return result;
        }
    }

    // Marks a domain as in use; assumes _lock held.
    private void TouchLocked(string key)
    {
        _touched[key] = Environment.TickCount64;
    }

    /// <summary>
    /// True when a domain's resolution is already known.
    /// </summary>
    public bool IsTracked(string domain)
    {
        var key = domain.TrimEnd('.').ToLowerInvariant();
        lock (_lock)
        {
            return _current.ContainsKey(key);
        }
    }

    /// <summary>
    /// The last-good IPv4 addresses tracked for a domain (whose /32 routes are already installed), or null
    /// when the domain is untracked or has no routable IPv4. Lets the DNS proxy answer a known domain from
    /// live routing state instead of re-querying the tunnel resolver. IPv4-only: an A answer is synthesized
    /// from these, so IPv6 is filtered here rather than trusting the stripV6 flag.
    /// </summary>
    public IReadOnlyList<string>? KnownIps(string domain)
    {
        var key = domain.TrimEnd('.').ToLowerInvariant();
        lock (_lock)
        {
            if (!_current.TryGetValue(key, out var set))
            {
                return null;
            }

            TouchLocked(key);
            var v4 = new List<string>();
            foreach (var ip in set)
            {
                if (!ip.Contains(':'))
                {
                    v4.Add(ip);
                }
            }

            return v4.Count > 0 ? v4 : null;
        }
    }

    /// <summary>
    /// Applies a domain's freshly resolved IPs additively (first resolution and accumulation of a domain's
    /// multiple live IPs): unions them with the cache and routes only the new ones. A previously routed IP is
    /// never dropped here, so a partial or transient answer cannot blackhole a working address. Eviction of a
    /// stale IP happens only via <see cref="Replace"/> (re-resolve) or <see cref="Remove"/> (left the lists).
    /// </summary>
    public void Add(string domain, IReadOnlyList<string> ips, bool persist = true)
    {
        var addedCidrs = new List<string>();
        lock (_lock)
        {
            var index = EnsureIndex();
            if (index is null)
            {
                logger.LogDebug("{Domain}: the tunnel adapter is not ready, so its addresses were left outside the tunnel", domain);
                return;
            }

            var key = domain.TrimEnd('.').ToLowerInvariant();
            // IPv4-only tunnel: never route IPv6 (no transit).
            var effective = stripV6 ? ips.Where(ip => !ip.Contains(':')) : ips;
            _current.TryGetValue(key, out var old);
            old ??= [];

            var added = new HashSet<string>();
            foreach (var ip in effective)
            {
                if (old.Contains(ip) || added.Contains(ip))
                {
                    continue;
                }

                var parsed = IPAddress.Parse(ip);
                if (KeptOffTunnel(parsed))
                {
                    continue;
                }

                // Record the IP only once its /32 route is actually installed, so routes, allowed-ips and
                // _current never drift - a failed route must not leave a routeless allowed-ip behind.
                if (routes.AddTunnelRoute(parsed, index.Value))
                {
                    added.Add(ip);
                    addedCidrs.Add(Cidr(parsed));
                }
                else
                {
                    logger.LogDebug("{Domain}: no route could be added for {Ip}, so that address stays outside the tunnel", domain, ip);
                }
            }

            if (added.Count == 0)
            {
                logger.LogTrace("{Domain}: already in the tunnel, nothing new to add", domain);
                return;
            }

            var union = new HashSet<string>(old);
            union.UnionWith(added);
            _current[key] = union;
            TouchLocked(key);

            logger.LogInformation("{Domain}: {Added} new address(es) now go through the tunnel, {Total} in total ({Ips})",
                key, added.Count, union.Count, Brief(union));
            if (RouteLog.Enabled)
            {
                RouteLog.Note($"resolve {key} -> [{string.Join(",", union)}] (+{addedCidrs.Count} route(s))");
            }

            // Persist the domain's full current set. Skipped when hydrating (persist:false) - it already came
            // from the DB, so re-writing the same rows would be pointless churn.
            if (persist)
            {
                var snapshot = union.ToList();
                var listId = _activeListId;
                EnqueuePersist(() => store.SaveDomainResolutionAsync(tunnelName, new DomainResolution(key, snapshot), listId));
            }
        }

        // Advertise off-lock: the UAPI pipe round-trip must not block concurrent resolves or serve-known lookups.
        // Route-before-answer still holds - the caller waits here before serving, just not while holding _lock.
        uapi.AddAllowedIps(tunnelName, peerPublicKey, addedCidrs);
        Adopt(addedCidrs);
    }

    // Hands the routed addresses to the cache as its own, so the two never install or reclaim the same address.
    private void Adopt(IReadOnlyList<string> cidrs)
    {
        if (routing is not null && Hosts(cidrs) is { Count: > 0 } addresses)
        {
            routing.Adopt(addresses);
        }
    }

    // Returns evicted addresses to the cache and to the flow tracker, so a later contact decides and routes them
    // again instead of being skipped as already handled.
    private void Forget(IReadOnlyList<string> cidrs)
    {
        if (Hosts(cidrs) is not { Count: > 0 } addresses)
        {
            return;
        }

        routing?.Forget(addresses);
        if (_onForgotten is { } sink)
        {
            sink([.. addresses.Select(address => address.ToString())]);
        }
    }

    // Drops the flow tracker's record of a destination it already handled.
    private volatile Action<IReadOnlyList<string>>? _onForgotten;

    // Reports destinations a matched app reached, so they are remembered and routed before it asks again.
    private volatile Action<IReadOnlyList<string>>? _onAppDestinations;

    // Tells whether an address is one the app memory holds; such an address is never reclaimed for being idle.
    private volatile Func<string, bool>? _remembered;

    // Aborts the half-open connections that left before these routes existed: their source address was chosen
    // without the route and cannot be changed, so the app has to open them again.
    private void Reset(IReadOnlyList<string> cidrs)
    {
        if (synReset is not null && Hosts(cidrs) is { Count: > 0 } addresses)
        {
            synReset.Abort(addresses);
        }
    }

    /// <summary>
    /// Attaches the sink told which destinations a matched app reached.
    /// </summary>
    public void SetAppDestinationSink(Action<IReadOnlyList<string>> sink)
    {
        _onAppDestinations = sink;
    }

    /// <summary>
    /// Attaches the sink told which destinations were released, so their dedupe records go with them.
    /// </summary>
    public void SetForgetSink(Action<IReadOnlyList<string>> sink)
    {
        _onForgotten = sink;
    }

    /// <summary>
    /// Attaches the test that says whether an app destination is remembered; without it such an address ages on the
    /// ordinary idle clock like any other.
    /// </summary>
    public void SetAppMemoryCheck(Func<string, bool> check)
    {
        _remembered = check;
    }

    private static List<IPAddress> Hosts(IReadOnlyList<string> cidrs)
    {
        var addresses = new List<IPAddress>(cidrs.Count);
        foreach (var cidr in cidrs)
        {
            var slash = cidr.IndexOf('/');
            if (slash > 0 && IPAddress.TryParse(cidr[..slash], out var parsed))
            {
                addresses.Add(parsed);
            }
        }

        return addresses;
    }

    /// <summary>
    /// Refreshes a rule-backed domain from a fresh resolution, EVICTING addresses that dropped out of the
    /// answer - unlike <see cref="Add"/>, which only unions. This is the self-heal path: when an actively-used
    /// domain is re-resolved through the tunnel, a stale or dead CDN IP is actually removed from routes and
    /// allowed-ips instead of lingering forever. Eviction is family-scoped: a v4-only answer never blanks the
    /// domain's v6 routes (and vice versa). An empty/failed answer is ignored so a lost re-resolve cannot
    /// blackhole a live domain.
    /// </summary>
    public void Replace(string domain, IReadOnlyList<string> ips)
    {
        var addedCidrs = new List<string>();
        var staleCidrs = new List<string>();
        lock (_lock)
        {
            var index = EnsureIndex();
            if (index is null)
            {
                return;
            }

            var key = domain.TrimEnd('.').ToLowerInvariant();
            var effective = new HashSet<string>(stripV6 ? ips.Where(ip => !ip.Contains(':')) : ips);
            if (effective.Count == 0)
            {
                return; // a failed/empty re-resolve must not blank a live domain
            }

            _current.TryGetValue(key, out var old);
            old ??= [];

            // Only families present in the fresh answer are eligible for eviction.
            var answerHasV4 = effective.Any(ip => !ip.Contains(':'));
            var answerHasV6 = effective.Any(ip => ip.Contains(':'));

            // Install routes for genuinely new IPs; keep only those whose /32 actually installed. Collect the
            // added CIDRs so the engine gets only the delta, not a rebuild of the whole set.
            var next = new HashSet<string>();
            foreach (var ip in effective)
            {
                if (old.Contains(ip))
                {
                    next.Add(ip);
                    continue;
                }

                var parsed = IPAddress.Parse(ip);
                if (KeptOffTunnel(parsed))
                {
                    continue;
                }

                if (routes.AddTunnelRoute(parsed, index.Value))
                {
                    next.Add(ip);
                    addedCidrs.Add(Cidr(parsed));
                }
            }

            // Carry over old IPs of a family the answer did not cover, so a v4-only refresh keeps v6 routes.
            foreach (var ip in old)
            {
                var isV6 = ip.Contains(':');
                if ((isV6 && !answerHasV6) || (!isV6 && !answerHasV4))
                {
                    next.Add(ip);
                }
            }

            _current[key] = next;
            TouchLocked(key);

            // Evict old IPs that dropped out and are no longer referenced by any other domain or the app set.
            List<IPAddress>? stale = null;
            foreach (var ip in old)
            {
                if (next.Contains(ip))
                {
                    continue;
                }

                if (!IsStillReferenced(ip, key))
                {
                    (stale ??= []).Add(IPAddress.Parse(ip));
                }
            }

            if (stale is not null)
            {
                routes.RemoveTunnelRoutes(stale, index.Value);
                staleCidrs.AddRange(stale.Select(Cidr));
            }

            logger.LogInformation("{Domain}: refreshed, {Total} address(es) in the tunnel, {Evicted} dead one(s) dropped ({Ips})",
                key, next.Count, stale?.Count ?? 0, Brief(next));
            if (RouteLog.Enabled)
            {
                RouteLog.Note($"re-resolve {key} -> [{string.Join(",", next)}] (evicted {stale?.Count ?? 0})");
            }

            // Persist the re-resolved set so the heal survives a restart.
            var snapshot = next.ToList();
            var listId = _activeListId;
            EnqueuePersist(() => store.SaveDomainResolutionAsync(tunnelName, new DomainResolution(key, snapshot), listId));
        }

        // Advertise the delta off-lock (O(new)); evictions already dropped their OS routes, and the pipe round-trip
        // must not block concurrent resolves / serve-known held under _lock.
        uapi.AddAllowedIps(tunnelName, peerPublicKey, addedCidrs);
        // Withdraw the evicted ones too: their routes are gone, and an allowed-ip left behind keeps accepting
        // inbound packets from that address for the rest of the session.
        uapi.QueueRemoveAllowedIps(tunnelName, peerPublicKey, staleCidrs);
        Adopt(addedCidrs);
        Forget(staleCidrs);
    }

    /// <summary>
    /// Snapshot of the currently tracked domain keys (used by list-update reconciliation to find
    /// domains that no longer match any routing rule).
    /// </summary>
    public IReadOnlyList<string> TrackedHosts()
    {
        lock (_lock)
        {
            return [.. _current.Keys];
        }
    }

    /// <summary>
    /// Drops a domain that left the routing lists: removes its /32 routes and allowed-ips (keeping IPs
    /// still referenced by another domain or the app set) and forgets its cached resolution.
    /// </summary>
    public void Remove(string domain)
    {
        var staleCidrs = new List<string>();
        lock (_lock)
        {
            var key = domain.TrimEnd('.').ToLowerInvariant();
            if (!_current.TryGetValue(key, out var ips))
            {
                return;
            }

            // Compute stale before dropping the key; IsStillReferenced already excludes this domain.
            List<IPAddress>? stale = null;
            foreach (var ip in ips)
            {
                if (!IsStillReferenced(ip, key))
                {
                    (stale ??= []).Add(IPAddress.Parse(ip));
                }
            }

            _current.Remove(key);
            _touched.Remove(key);

            // Forget the persisted resolution too (domain left the routing lists).
            EnqueuePersist(() => store.DeleteDomainResolutionAsync(tunnelName, key));

            var index = EnsureIndex();
            if (index is not null && stale is not null)
            {
                routes.RemoveTunnelRoutes(stale, index.Value);
                staleCidrs.AddRange(stale.Select(Cidr));
            }

            logger.LogInformation("{Domain}: no longer matches any rule; {Count} route(s) removed, its traffic now leaves directly", key, stale?.Count ?? 0);
            if (RouteLog.Enabled)
            {
                RouteLog.Note($"untrack {key} (-{stale?.Count ?? 0} route(s))");
            }
        }

        // Off-lock, and withdrawn from the engine too: a name that left the lists must not keep an advertisement.
        uapi.QueueRemoveAllowedIps(tunnelName, peerPublicKey, staleCidrs);
        Forget(staleCidrs);
    }

    /// <summary>
    /// Hydrates a single matched domain from the persisted cache on demand (no bulk warm start). When the
    /// domain is not already tracked, its last-good IPs are loaded from the DB and installed like a fresh
    /// <see cref="Add"/> - so a queried domain seen in a previous session skips the (lossy) tunnel resolver.
    /// Returns the routable v4 IPs for a serve-known answer, or null when nothing is cached (caller resolves).
    /// </summary>
    public async Task<IReadOnlyList<string>?> TryHydrateFromCacheAsync(string domain, Func<string, bool> isStillTunneled, CancellationToken ct = default)
    {
        var key = domain.TrimEnd('.').ToLowerInvariant();
        lock (_lock)
        {
            if (_current.ContainsKey(key))
            {
                return null; // already in memory; the caller's KnownIps path serves it
            }
        }

        DomainResolution? cached;
        try
        {
            cached = await store.GetDomainResolutionAsync(tunnelName, key, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "domain cache lookup failed for {Domain}", key);
            return null;
        }

        if (cached is null || cached.Ips.Count == 0)
        {
            return null; // nothing cached -> caller resolves through the tunnel
        }

        // Re-check membership after the await: a list edit during the DB read may have swapped the matcher so
        // this domain just left the lists (same guard as the resolve/Track path) - never pin a departed domain.
        if (!isStillTunneled(domain))
        {
            return null;
        }

        // Install the cached set's routes/allowed-ips without a re-resolve; persist:false since it is the DB.
        // Isolated like Track: an IPC/route failure during tunnel churn must not drop the query - returning null
        // falls the caller through to a real resolve (which answers SERVFAIL) instead of leaving it unanswered.
        try
        {
            Add(key, cached.Ips, persist: false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "hydrate route install failed for {Domain}", key);
            return null;
        }

        return KnownIps(key);
    }

    // Serialises this tunnel's resolution writes so a later change never lands in the DB before an earlier one.
    private void EnqueuePersist(Func<Task> op)
    {
        lock (_persistLock)
        {
            _persistTail = _persistTail.ContinueWith(
                async _ =>
                {
                    try
                    {
                        await op().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug(ex, "persist domain resolution failed for {Tunnel}", tunnelName);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default).Unwrap();
        }
    }

    /// <summary>
    /// Watches for routing-list changes and reconciles the static geoip ranges + rebuilds the proxy matcher
    /// live (so a source refresh takes effect without reconnect). There is no warm start and no bulk
    /// re-resolve: rule-backed domains are (re)resolved purely on demand by the DNS proxy.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (routes.FindTunnelIndex(tunnelName) is null)
            {
                await Task.Delay(500, ct);
            }

            // No bulk DB warm start: resolutions are hydrated lazily per queried domain. Release any awaiter.
            _warmStart.TrySetResult();

            // Seed the active routing list id so persisted rows are tagged for list-scoped cleanup.
            try
            {
                var listId = await store.GetActiveRoutingListIdAsync(tunnelName) ?? 0;
                lock (_lock)
                {
                    _activeListId = listId;
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "initial active routing list id lookup failed for {Tunnel}", tunnelName);
            }

            // A rule change is announced by the agent over the runtime pipe; the only work left on this loop is
            // reclaiming names nobody visits.
            while (true)
            {
                await Task.Delay(Volatile.Read(ref _evictIntervalMs), ct);
                try
                {
                    EvictIdle();
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "domain eviction failed for {Tunnel}", tunnelName);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Release the warm-start waiter so any awaiter never hangs.
            _warmStart.TrySetResult();
        }
        catch (Exception ex)
        {
            _warmStart.TrySetResult();
            logger.LogError(ex, "domain tracker for {Tunnel} stopped", tunnelName);
        }
    }

    // Last contact with a name: its own resolutions, or traffic to any address it holds. Without the second the
    // name would be evicted out from under a live flow whenever the resolver answered from its own cache.
    private long LastContactLocked(string domain, long resolved)
    {
        if (!_current.TryGetValue(domain, out var ips) || routing?.LastContact(ips) is not { } contact)
        {
            return resolved;
        }

        return Math.Max(resolved, contact);
    }

    // Drops what nobody has touched for the idle window - names and app destinations alike - releasing their /32
    // routes and allowed-ips, while the persisted resolution stays as the hydration cache the next query reads.
    private void EvictIdle()
    {
        var now = Environment.TickCount64;
        var idleTtlMs = Volatile.Read(ref _idleTtlMs);
        var domains = new List<string>();
        var apps = new List<string>();
        var released = new HashSet<string>(StringComparer.Ordinal);
        List<IPAddress>? stale = null;
        lock (_lock)
        {
            foreach (var pair in _touched)
            {
                if (now - LastContactLocked(pair.Key, pair.Value) > idleTtlMs)
                {
                    domains.Add(pair.Key);
                }
            }

            foreach (var key in domains)
            {
                _touched.Remove(key);
                if (_current.Remove(key, out var ips))
                {
                    released.UnionWith(ips);
                }
            }

            // An app destination ages like a name: its clock is the traffic to it, whatever routed it here. A
            // remembered one is exempt - the app dials it by address, so the attempt that would earn the route back
            // is the very attempt that loses its answer.
            foreach (var pair in _appIps)
            {
                if (now - LastTrafficLocked(pair.Key, pair.Value) > idleTtlMs && !Remembered(pair.Key))
                {
                    apps.Add(pair.Key);
                }
            }

            foreach (var ip in apps)
            {
                _appIps.Remove(ip);
                released.Add(ip);
            }

            foreach (var ip in released)
            {
                if (!IsStillReferenced(ip, string.Empty))
                {
                    (stale ??= []).Add(IPAddress.Parse(ip));
                }
            }
        }

        if (stale is null)
        {
            return;
        }

        var index = EnsureIndex();
        if (index is not null)
        {
            routes.RemoveTunnelRoutes(stale, index.Value);
        }

        var staleCidrs = stale.Select(Cidr).ToList();
        uapi.QueueRemoveAllowedIps(tunnelName, peerPublicKey, staleCidrs);
        Forget(staleCidrs);
        logger.LogInformation("{Domains} domain(s) and {Apps} app destination(s) went unused; {Routes} route(s) removed, they return to the tunnel when used again",
            domains.Count, apps.Count, stale.Count);
    }

    // Last traffic to a single address, as the routing cache saw it; without it an app destination would age on the
    // moment it was routed and leave in the middle of a live transfer.
    private long LastTrafficLocked(string ip, long routed)
    {
        return routing?.LastContact([ip]) is { } contact ? Math.Max(routed, contact) : routed;
    }

    // Whether the app memory holds this address.
    private bool Remembered(string ip)
    {
        if (_remembered is not { } check)
        {
            return false;
        }

        try
        {
            return check(ip);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "the remembered app destinations could not be consulted for {Ip}", ip);
            return false;
        }
    }

    /// <summary>
    /// Applies a routing list the agent just persisted: retags persisted rows and rebuilds the domain matcher.
    /// Newly listed domains are not pre-resolved - they resolve when first queried.
    /// </summary>
    public void ApplyList(AmneziaGeo.Decl.ActiveRoutingListMaterialization current, CancellationToken ct)
    {
        if (current.Generation == _knownGeneration)
        {
            return;
        }

        // With lazy ranges nothing is materialized up front, so there is no static set to reconcile - the routing
        // cache re-decides each destination against the new rules.
        if (!lazyRanges)
        {
            ReconcileStaticRoutes(current.Routes);
        }

        // Retag before the matcher rebuild: a domain newly matched under the new list must persist with the
        // correct list_id, not the previous one.
        lock (_lock)
        {
            _activeListId = current.ListId;
        }

        _onGeoDomainsChanged?.Invoke(current.Domains, ct);
        _knownGeneration = current.Generation;
    }

    /// <summary>
    /// Routes per-app discovered remote IPs through the tunnel; only newly seen IPs install a route.
    /// </summary>
    public bool UpdateAppIps(IReadOnlyList<string> ips)
    {
        List<string> addedCidrs;
        bool allHandled;
        lock (_lock)
        {
            (allHandled, addedCidrs) = RouteAppIpsLocked(ips);
        }

        // Advertise off-lock so the pipe round-trip never blocks the DNS resolve / serve-known path on _lock.
        uapi.AddAllowedIps(tunnelName, peerPublicKey, addedCidrs);
        Adopt(addedCidrs);
        Reset(addedCidrs);
        return allHandled;
    }

    // Installs /32(/128) routes for app IPs; assumes _lock held. Returns the CIDRs whose routes were installed so
    // the caller advertises them to the engine OFF-lock, plus whether every input IP was handled (else caller retries).
    private (bool AllHandled, List<string> AddedCidrs) RouteAppIpsLocked(IEnumerable<string> ips)
    {
        var addedCidrs = new List<string>();
        var index = EnsureIndex();
        if (index is null)
        {
            return (false, addedCidrs); // adapter not up; caller retries
        }

        var allHandled = true;
        var now = Environment.TickCount64;
        foreach (var ip in ips)
        {
            // v4-only tunnel: never route IPv6.
            if (stripV6 && ip.Contains(':'))
            {
                continue;
            }

            if (_appIps.ContainsKey(ip))
            {
                _appIps[ip] = now;
                continue;
            }

            // Bound the set so a chatty app cannot grow it without limit.
            if (_appIps.Count >= 8192)
            {
                allHandled = false; // not recorded; caller retries
                break;
            }

            // Record the IP only once its /32 route is installed, so routes and allowed-ips stay in sync.
            var parsed = IPAddress.Parse(ip);
            if (KeptOffTunnel(parsed))
            {
                continue;
            }

            var ok = routes.AddTunnelRoute(parsed, index.Value);
            if (ok)
            {
                _appIps[ip] = now;
                addedCidrs.Add(Cidr(parsed));
                logger.LogTrace("{Ip}: routed into the tunnel for a matched app", ip);
            }
            else
            {
                allHandled = false; // route add failed; caller retries
                logger.LogDebug("{Ip}: no route could be added for a matched app; this connection leaves directly, will retry", ip);
            }
        }

        return (allHandled, addedCidrs);
    }

    /// <summary>
    /// Records a real DNS resolution into the app-promotion hint cache (name->IPs + reverse index). When the
    /// name is already app-promoted, its IPs are routed immediately so a promoted app domain's fresh sibling
    /// CDN IPs are born tunnel-side (route-before-answer).
    /// </summary>
    public void NoteResolution(string name, IReadOnlyList<string> ips)
    {
        var key = name.TrimEnd('.').ToLowerInvariant();
        var addedCidrs = new List<string>();
        lock (_lock)
        {
            // Bound the hint cache; wholesale clear mirrors the DNS cache eviction pattern.
            if (_ipToNames.Count >= MaxLearnedIps)
            {
                _ipToNames.Clear();
                _nameToIps.Clear();
                _promotedApps.Clear();
            }

            var fwd = _nameToIps.TryGetValue(key, out var f) ? f : (_nameToIps[key] = new(StringComparer.Ordinal));
            foreach (var ip in ips)
            {
                fwd.Add(ip);
                var rev = _ipToNames.TryGetValue(ip, out var r) ? r : (_ipToNames[ip] = new(StringComparer.Ordinal));
                rev.Add(key);
            }

            if (_promotedApps.Contains(key))
            {
                addedCidrs = RouteAppIpsLocked(ips).AddedCidrs;
            }
        }

        // Advertise off-lock (empty no-op unless the domain is app-promoted).
        uapi.AddAllowedIps(tunnelName, peerPublicKey, addedCidrs);
        Adopt(addedCidrs);
    }

    /// <summary>
    /// Names a resolution answered with this address, newest run's knowledge only. Lets a rule by name reach an
    /// address that is already held, without the name being asked again.
    /// </summary>
    public IReadOnlyList<string> NamesOf(string ip)
    {
        lock (_lock)
        {
            return _ipToNames.TryGetValue(ip, out var names) ? [.. names] : [];
        }
    }

    /// <summary>
    /// Raised when an app's traffic promotes a domain, so its later queries resolve through the tunnel resolver
    /// instead of the local one.
    /// </summary>
    public event Action<string>? DomainPromoted;

    /// <summary>
    /// Matched apps touched remote IPs: routes those destinations, promotes the domains they resolved to so future
    /// resolutions route before the answer, and routes each promoted domain's remaining addresses. One lock and one
    /// advertisement for the whole batch. Returns false when an IP was left unrouted, so the caller retries.
    /// </summary>
    public bool NoteAppRemotes(IReadOnlyList<string> ips)
    {
        List<string> addedCidrs;
        var promoted = default(List<string>);
        bool allHandled;
        lock (_lock)
        {
            (allHandled, addedCidrs) = RouteAppIpsLocked(ips);
            foreach (var ip in ips)
            {
                // Promote only from an IP mapped to a single known domain: a shared anycast edge (Cloudflare) would
                // otherwise promote unrelated sites that merely share it, routing their process-agnostic traffic too.
                if (!_ipToNames.TryGetValue(ip, out var names) || names.Count != 1)
                {
                    continue;
                }

                var name = Single(names);
                if (!_promotedApps.Add(name))
                {
                    continue;
                }

                logger.LogInformation("{Name}: recognised as a domain of a tunneled app (seen at {Ip}); its addresses now go through the tunnel", name, ip);
                (promoted ??= []).Add(name);

                // The domain's other addresses serve the same app, and a CDN hands them out in rotation: route them
                // now, or the app's next attempt picks a sibling that has no route yet and leaves the tunnel.
                if (_nameToIps.TryGetValue(name, out var siblings))
                {
                    var (siblingsHandled, siblingCidrs) = RouteAppIpsLocked(siblings);
                    allHandled &= siblingsHandled;
                    addedCidrs.AddRange(siblingCidrs);
                }
            }
        }

        // Advertise off-lock: the pipe round-trip must not hold the resolve path.
        uapi.AddAllowedIps(tunnelName, peerPublicKey, addedCidrs);
        Adopt(addedCidrs);
        Reset(addedCidrs);
        if (promoted is not null)
        {
            foreach (var name in promoted)
            {
                DomainPromoted?.Invoke(name);
            }
        }

        _onAppDestinations?.Invoke(ips);
        return allHandled;
    }

    // The element of a one-item set.
    private static string Single(HashSet<string> set)
    {
        foreach (var item in set)
        {
            return item;
        }

        return string.Empty;
    }

    // True when an IP is still held by another tracked domain or _appIps, excluding the just-applied set.
    private bool IsStillReferenced(string ip, string excludeKey)
    {
        if (_appIps.ContainsKey(ip))
        {
            return true;
        }

        foreach (var (k, set) in _current)
        {
            if (k != excludeKey && set.Contains(ip))
            {
                return true;
            }
        }

        return false;
    }

    // /32 for IPv4, /128 for IPv6; single source of truth for the prefix.
    // A destination the cache classified as Direct or Block must not be pulled into the tunnel: the cache owns that
    // decision, and two host routes on different interfaces would be settled by metric instead of by the rules.
    private bool KeptOffTunnel(IPAddress address)
    {
        if (routing is null)
        {
            return false;
        }

        var verdict = routing.Classify(address);
        if (verdict is not (RouteVerdict.Direct or RouteVerdict.Block))
        {
            return false;
        }

        logger.LogDebug("{Address}: kept out of the tunnel, the routing rules classify it as {Verdict}", address, verdict);
        return true;
    }

    // First few addresses, so a CDN answer of twenty does not fill the line.
    private static string Brief(IReadOnlyCollection<string> ips)
    {
        const int shown = 4;
        return ips.Count <= shown ? string.Join(", ", ips) : string.Join(", ", ips.Take(shown)) + $", +{ips.Count - shown} more";
    }

    private static string Cidr(IPAddress ip)
    {
        var prefix = ip.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;
        return $"{ip}/{prefix}";
    }

    // Reconciles the routing list's static geoip ranges live on a list change: adds ranges new to the list and
    // removes ranges that left it. Only the list subset (_listRoutes) is touched; infrastructure routes such as
    // the tunnel-DNS /32s are never removed.
    private void ReconcileStaticRoutes(IReadOnlyList<string> freshRoutes)
    {
        var addedCidrs = new List<string>();
        lock (_lock)
        {
            var index = EnsureIndex();
            if (index is null)
            {
                return;
            }

            // v4-only tunnel: never route IPv6.
            var fresh = new HashSet<string>(
                stripV6 ? freshRoutes.Where(c => !c.Contains(':')) : freshRoutes,
                StringComparer.Ordinal);

            foreach (var cidr in fresh)
            {
                if (!_listRoutes.Add(cidr))
                {
                    continue;
                }

                // Advertise the range only once its route is installed, so routes and allowed-ips stay in sync.
                if (routes.AddTunnelCidr(cidr, index.Value))
                {
                    _staticRoutes.Add(cidr);
                    addedCidrs.Add(cidr);
                }
                else
                {
                    _listRoutes.Remove(cidr);
                }
            }

            // Remove ranges that left the list. Delete the OS route FIRST (traffic then falls back to the default
            // route = direct); rebuild allowed-ips after. The reverse order would leave a route-to-tunnel with no
            // matching allowed-ip = blackhole.
            var removed = 0;
            foreach (var cidr in _listRoutes.Where(c => !fresh.Contains(c)).ToList())
            {
                _listRoutes.Remove(cidr);
                _staticRoutes.Remove(cidr);
                routes.RemoveTunnelCidr(cidr, index.Value);
                removed++;
            }

            if (addedCidrs.Count > 0)
            {
                logger.LogInformation("{Count} new address range(s) added to the tunnel without reconnecting", addedCidrs.Count);
            }

            if (removed > 0)
            {
                logger.LogInformation("{Count} address range(s) left the rules and were removed from the tunnel", removed);
            }

        }

        // Advertise added ranges off-lock (O(added)); removed ranges need no engine update - their OS routes are
        // already gone, and the pipe round-trip must not block resolves waiting on _lock.
        uapi.AddAllowedIps(tunnelName, peerPublicKey, addedCidrs);
    }

    private uint? EnsureIndex()
    {
        _interfaceIndex ??= routes.FindTunnelIndex(tunnelName);
        return _interfaceIndex;
    }
}
