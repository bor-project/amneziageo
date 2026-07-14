using System.Net;
using System.Net.Sockets;
using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
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
    IReadOnlyList<GeoDomain> geoDomains,
    int refreshSeconds,
    bool stripV6)
{
    private readonly object _lock = new();
    private readonly Dictionary<string, HashSet<string>> _current = [];
    // App-discovered remote IPs, unioned into allowed-ips so the watcher and DNS path share one authority.
    private readonly HashSet<string> _appIps = [];

    // App-promotion hint cache (non-authoritative): learned name->IPs and its reverse index, plus the set of
    // app-promoted domains. Feeds route-before-answer for a matched app's sibling CDN IPs; stale entries are
    // harmless (at worst one dead /32), so these are NOT mirrored on Add/Replace/Remove eviction.
    private readonly Dictionary<string, HashSet<string>> _nameToIps = [];
    private readonly Dictionary<string, HashSet<string>> _ipToNames = [];
    private readonly HashSet<string> _promotedApps = new(StringComparer.Ordinal);
    private const int MaxLearnedIps = 8192;

    // App INTERSECT geo gate: an app-touched destination is tunneled only when a domain it resolved to matches
    // the active list's geosite rules. Rebuilt on materialization generation change, mirroring the DnsProxy matcher.
    private volatile DomainMatcher? _geoMatcher = geoDomains.Count > 0 ? new DomainMatcher(geoDomains) : null;

    // All static geoip CIDRs advertised in allowed-ips: list ranges + connect infrastructure (tunnel-DNS /32s).
    private readonly HashSet<string> _staticRoutes = new(staticRoutes, StringComparer.Ordinal);

    // The reconcilable subset: ranges that came from the routing list. Only these are removed when a list drops
    // them - infrastructure routes (in _staticRoutes but not here, e.g. the tunnel resolver /32s) are never touched.
    private readonly HashSet<string> _listRoutes = new(stripV6 ? listRoutes.Where(c => !c.Contains(':')) : listRoutes, StringComparer.Ordinal);

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
        lock (_lock)
        {
            var index = EnsureIndex();
            logger.LogDebug("DIAG track {Domain} ips=[{Ips}] index={Index} stripV6={StripV6}", domain, string.Join(",", ips), index, stripV6);
            if (index is null)
            {
                return;
            }

            var key = domain.TrimEnd('.').ToLowerInvariant();
            // IPv4-only tunnel: never route IPv6 (no transit).
            var effective = stripV6 ? ips.Where(ip => !ip.Contains(':')) : ips;
            _current.TryGetValue(key, out var old);
            old ??= [];

            var addedCidrs = new List<string>();
            var added = new HashSet<string>();
            foreach (var ip in effective)
            {
                if (old.Contains(ip) || added.Contains(ip))
                {
                    continue;
                }

                var parsed = IPAddress.Parse(ip);
                // Record the IP only once its /32 route is actually installed, so routes, allowed-ips and
                // _current never drift - a failed route must not leave a routeless allowed-ip behind.
                if (routes.AddTunnelRoute(parsed, index.Value))
                {
                    added.Add(ip);
                    addedCidrs.Add(Cidr(parsed));
                }
                else
                {
                    logger.LogDebug("DIAG track add {Ip}/32 -> ifIndex {Index} ok=false (skipped)", ip, index.Value);
                }
            }

            if (added.Count == 0)
            {
                logger.LogDebug("DIAG track {Domain} no new routable ips", domain);
                return;
            }

            var union = new HashSet<string>(old);
            union.UnionWith(added);
            _current[key] = union;

            logger.LogInformation("resolved {Domain} -> {Ips}", key, string.Join(", ", union));
            if (RouteLog.Enabled)
            {
                RouteLog.Note($"resolve {key} -> [{string.Join(",", union)}] (+{addedCidrs.Count} route(s))");
            }

            // Advertise only the newly added IPs incrementally so route-before-answer stays O(new).
            uapi.AddAllowedIps(tunnelName, peerPublicKey, addedCidrs);

            // Persist the domain's full current set. Skipped when hydrating (persist:false) - it already came
            // from the DB, so re-writing the same rows would be pointless churn.
            if (persist)
            {
                var snapshot = union.ToList();
                var listId = _activeListId;
                EnqueuePersist(() => store.SaveDomainResolutionAsync(tunnelName, new DomainResolution(key, snapshot), listId));
            }
        }
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

            // Install routes for genuinely new IPs; keep only those whose /32 actually installed.
            var next = new HashSet<string>();
            foreach (var ip in effective)
            {
                if (old.Contains(ip))
                {
                    next.Add(ip);
                    continue;
                }

                if (routes.AddTunnelRoute(IPAddress.Parse(ip), index.Value))
                {
                    next.Add(ip);
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
            }

            // One authoritative allowed-ips rebuild reflecting both the additions and the evictions.
            uapi.SetAllowedIps(tunnelName, peerPublicKey, BuildAllowedIps());

            logger.LogInformation("re-resolved {Domain} -> {Ips} (evicted {Evicted})", key, string.Join(", ", next), stale?.Count ?? 0);
            if (RouteLog.Enabled)
            {
                RouteLog.Note($"re-resolve {key} -> [{string.Join(",", next)}] (evicted {stale?.Count ?? 0})");
            }

            // Persist the re-resolved set so the heal survives a restart.
            var snapshot = next.ToList();
            var listId = _activeListId;
            EnqueuePersist(() => store.SaveDomainResolutionAsync(tunnelName, new DomainResolution(key, snapshot), listId));
        }
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

            // Forget the persisted resolution too (domain left the routing lists).
            EnqueuePersist(() => store.DeleteDomainResolutionAsync(tunnelName, key));

            var index = EnsureIndex();
            if (index is not null && stale is not null)
            {
                routes.RemoveTunnelRoutes(stale, index.Value);
                uapi.SetAllowedIps(tunnelName, peerPublicKey, BuildAllowedIps());
            }

            logger.LogInformation("untracked {Domain}: left routing lists (-{Count} route(s))", key, stale?.Count ?? 0);
            if (RouteLog.Enabled)
            {
                RouteLog.Note($"untrack {key} (-{stale?.Count ?? 0} route(s))");
            }
        }
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
            while (routes.FindInterfaceIndex(tunnelName) is null)
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

            // Leave _knownGeneration null so the first poll reconciles geoip deltas and seeds the matcher.
            // Cap at 5s so a live routing-list edit lands quickly without a reconnect.
            var pollInterval = TimeSpan.FromSeconds(Math.Clamp(Math.Min(refreshSeconds, 5), 1, 60));
            while (true)
            {
                await Task.Delay(pollInterval, ct);

                try
                {
                    // Cheap generation probe first; pull (and deserialize) the full materialization only on change.
                    var generation = await store.GetActiveRoutingListGenerationAsync(tunnelName);
                    if (generation is not null && generation != _knownGeneration)
                    {
                        var current = await store.GetActiveRoutingListMaterializationAsync(tunnelName);
                        if (current is not null)
                        {
                            ReconcileStaticRoutes(current.Routes);
                            // Tag persisted rows with the new list id BEFORE the matcher rebuild: a domain newly
                            // matched under the new list must persist with the correct list_id, not the previous one.
                            lock (_lock)
                            {
                                _activeListId = current.ListId;
                            }
                            // Rebuild the matcher and prune domains that left the lists. Newly listed domains are
                            // NOT pre-resolved - they resolve on demand when first queried.
                            _onGeoDomainsChanged?.Invoke(current.Domains, ct);
                            // Rebuild the app-gate matcher so app INTERSECT geo tracks live list edits, like the proxy matcher.
                            _geoMatcher = current.Domains.Count > 0 ? new DomainMatcher(current.Domains) : null;
                            _knownGeneration = current.Generation;
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "geo list signal poll failed for {Tunnel}", tunnelName);
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

    /// <summary>
    /// Routes per-app discovered remote IPs through the tunnel; only newly seen IPs install a route.
    /// </summary>
    public bool UpdateAppIps(IReadOnlyList<string> ips)
    {
        lock (_lock)
        {
            return RouteAppIpsLocked(ips);
        }
    }

    // Admits an app destination to the tunnel. With a geosite configured, only destinations whose resolved domain
    // matches it pass (app INTERSECT geo); with none, every app destination passes (route-all fallback). Reverse
    // index is built by NoteResolution; no geo-universe pre-materialization. Assumes _lock held.
    private bool AppDestAllowed(string ip) =>
        _geoMatcher is not { } m
        || (_ipToNames.TryGetValue(ip, out var names) && names.Any(m.IsTunneled));

    // Installs /32(/128) routes + allowed-ips for app IPs; assumes _lock held. Shared by the watcher path and
    // the app-domain promotion path.
    private bool RouteAppIpsLocked(IReadOnlyList<string> ips)
    {
        var index = EnsureIndex();
        if (index is null)
        {
            return false; // adapter not up; caller retries
        }

        var addedCidrs = new List<string>();
        var allHandled = true;
        foreach (var ip in ips)
        {
            // v4-only tunnel: never route IPv6.
            if (stripV6 && ip.Contains(':'))
            {
                continue;
            }

            // Gate: route an app destination only when it matches a configured geo rule.
            if (!AppDestAllowed(ip))
            {
                continue;
            }

            if (_appIps.Contains(ip))
            {
                continue;
            }

            // Bound the set so a chatty app cannot grow it without limit.
            if (_appIps.Count >= 8192)
            {
                allHandled = false; // not recorded; caller retries
                break;
            }

            // Advertise the IP only once its /32 route is installed, so routes and allowed-ips stay in sync.
            var parsed = IPAddress.Parse(ip);
            var ok = routes.AddTunnelRoute(parsed, index.Value);
            logger.LogDebug("DIAG app route add {Ip}/32 -> ifIndex {Index} ok={Ok}", ip, index.Value, ok);
            if (ok)
            {
                _appIps.Add(ip);
                addedCidrs.Add(Cidr(parsed));
            }
            else
            {
                allHandled = false; // route add failed; caller retries
            }
        }

        // Advertise new IPs incrementally; the set only grows within a session.
        uapi.AddAllowedIps(tunnelName, peerPublicKey, addedCidrs);
        return allHandled;
    }

    /// <summary>
    /// Records a real DNS resolution into the app-promotion hint cache (name->IPs + reverse index). When the
    /// name is already app-promoted, its IPs are routed immediately so a promoted app domain's fresh sibling
    /// CDN IPs are born tunnel-side (route-before-answer).
    /// </summary>
    public void NoteResolution(string name, IReadOnlyList<string> ips)
    {
        var key = name.TrimEnd('.').ToLowerInvariant();
        lock (_lock)
        {
            // Bound the hint cache; wholesale clear mirrors the DNS cache eviction pattern.
            if (_ipToNames.Count >= MaxLearnedIps)
            {
                _ipToNames.Clear();
                _nameToIps.Clear();
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
                RouteAppIpsLocked(ips);
            }
        }
    }

    /// <summary>
    /// A matched app touched a remote IP: promotes the domain(s) that IP resolved to (only geo-matching ones when a
    /// geosite is configured) so their future resolutions route before the answer. Without a geosite the whole known
    /// sibling set is pre-routed (route-all); with one, the gated sink routes just the touched IP.
    /// </summary>
    public void NoteAppRemote(string ip)
    {
        lock (_lock)
        {
            if (!_ipToNames.TryGetValue(ip, out var names))
            {
                return;
            }

            var matcher = _geoMatcher;
            foreach (var name in names)
            {
                if (matcher is not null && !matcher.IsTunneled(name))
                {
                    continue;
                }

                if (!_promotedApps.Add(name))
                {
                    continue;
                }

                logger.LogInformation("app promoted domain {Name} via {Ip}", name, ip);
                if (matcher is null && _nameToIps.TryGetValue(name, out var sib))
                {
                    RouteAppIpsLocked(sib.ToList());
                }
            }

            RouteAppIpsLocked(new[] { ip });
        }
    }

    // True when an IP is still held by another tracked domain or _appIps, excluding the just-applied set.
    private bool IsStillReferenced(string ip, string excludeKey)
    {
        if (_appIps.Contains(ip))
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
    private static string Cidr(IPAddress ip)
    {
        var prefix = ip.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;
        return $"{ip}/{prefix}";
    }

    private List<string> BuildAllowedIps()
    {
        var all = new List<string>(_staticRoutes);
        foreach (var set in _current.Values)
        {
            foreach (var ip in set)
            {
                all.Add(Cidr(IPAddress.Parse(ip)));
            }
        }

        // App-discovered IPs share the same allowed-ips authority.
        foreach (var ip in _appIps)
        {
            all.Add(Cidr(IPAddress.Parse(ip)));
        }

        return all;
    }

    // Reconciles the routing list's static geoip ranges live on a list change: adds ranges new to the list and
    // removes ranges that left it. Only the list subset (_listRoutes) is touched; infrastructure routes such as
    // the tunnel-DNS /32s are never removed.
    private void ReconcileStaticRoutes(IReadOnlyList<string> freshRoutes)
    {
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

            var addedCidrs = new List<string>();
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
                logger.LogInformation("geo cache: applied {Count} new range(s) live to {Tunnel}", addedCidrs.Count, tunnelName);
            }

            if (removed > 0)
            {
                logger.LogInformation("geo cache: removed {Count} departed range(s) live from {Tunnel}", removed, tunnelName);
            }

            // One allowed-ips update reflecting both directions; full replace when anything was removed.
            if (removed > 0)
            {
                uapi.SetAllowedIps(tunnelName, peerPublicKey, BuildAllowedIps());
            }
            else if (addedCidrs.Count > 0)
            {
                uapi.AddAllowedIps(tunnelName, peerPublicKey, addedCidrs);
            }
        }
    }

    private uint? EnsureIndex()
    {
        _interfaceIndex ??= routes.FindInterfaceIndex(tunnelName);
        return _interfaceIndex;
    }
}
