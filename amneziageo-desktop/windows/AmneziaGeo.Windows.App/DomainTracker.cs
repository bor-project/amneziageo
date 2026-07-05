using System.Net;
using System.Net.Sockets;
using AmneziaGeo.Decl;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Resolves tunneled domains to IPs, persists them, and keeps them fresh by re-resolving.
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
    int refreshSeconds,
    bool stripV6)
{
    private readonly object _lock = new();
    private readonly Dictionary<string, HashSet<string>> _current = [];
    // App-discovered remote IPs, unioned into allowed-ips so the watcher and DNS path share one authority.
    private readonly HashSet<string> _appIps = [];

    // All static geoip CIDRs advertised in allowed-ips: list ranges + connect infrastructure (tunnel-DNS /32s).
    private readonly HashSet<string> _staticRoutes = new(staticRoutes, StringComparer.Ordinal);

    // The reconcilable subset: ranges that came from the routing list. Only these are removed when a list drops
    // them - infrastructure routes (in _staticRoutes but not here, e.g. the tunnel resolver /32s) are never touched.
    private readonly HashSet<string> _listRoutes = new(stripV6 ? listRoutes.Where(c => !c.Contains(':')) : listRoutes, StringComparer.Ordinal);

    // Baselines for the poll signals: list materialization generation and global resolve epoch.
    private long? _knownGeneration;
    private long _knownResolveEpoch;

    // Routing list the tunnel currently projects; tags persisted resolutions so a list's cache can be cleaned
    // on removal. Read/written under _lock. 0 = full-tunnel / no-list.
    private long _activeListId;

    // Live geo-domain sink; rebuilt on materialization generation change so a source refresh takes effect without reconnect.
    private volatile Action<IReadOnlyList<GeoDomain>, CancellationToken>? _onGeoDomainsChanged;

    private uint? _interfaceIndex;
    private readonly TaskCompletionSource _warmStart = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Serializes cache persistence: writes run in enqueue order so a DELETE never overtakes a later SAVE.
    private readonly object _persistLock = new();
    private Task _persistChain = Task.CompletedTask;

    /// <summary>
    /// Completes once the DB-cache warm start is applied.
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
    /// Applies a domain's freshly resolved IPs additively (used by both the hot path and the list-update
    /// re-resolve): unions them with the cache and routes only the new ones. A previously routed IP is never
    /// dropped here, so a partial or transient answer cannot blackhole a working address. Domains that leave
    /// the routing lists are dropped separately via <see cref="Remove"/>.
    /// </summary>
    public void Add(string domain, IReadOnlyList<string> ips)
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

            var listId = _activeListId;
            EnqueuePersist(() => PersistAsync(listId, new DomainResolution(key, [.. union])));
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

            EnqueuePersist(() => DeleteResolutionAsync(key));
        }
    }

    // Serializes cache writes so a fire-and-forget DELETE never overtakes a later SAVE for the same domain
    // (or vice versa): both are enqueued under _lock, and this chain runs them in that order off the hot path.
    private void EnqueuePersist(Func<Task> op)
    {
        lock (_persistLock)
        {
            var previous = _persistChain;
            _persistChain = RunSequentialAsync(previous, op);
        }
    }

    private static async Task RunSequentialAsync(Task previous, Func<Task> op)
    {
        try
        {
            await previous.ConfigureAwait(false);
        }
        catch
        {
            // A prior write already logged its own failure; never let it break the chain.
        }

        await op().ConfigureAwait(false);
    }

    private async Task DeleteResolutionAsync(string domain)
    {
        try
        {
            await store.DeleteDomainResolutionAsync(tunnelName, domain);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "delete of {Domain} resolution failed", domain);
        }
    }

    private async Task PersistAsync(long listId, DomainResolution resolution)
    {
        try
        {
            await store.SaveDomainResolutionAsync(tunnelName, resolution, listId);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "persist of {Domain} resolution failed", resolution.Domain);
        }
    }

    /// <summary>
    /// Applies cached resolutions (warm start), then watches for routing-list changes and re-resolves
    /// only when a list actually changes. There is no periodic timer-driven re-resolve.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (routes.FindInterfaceIndex(tunnelName) is null)
            {
                await Task.Delay(500, ct);
            }

            try
            {
                SeedFromCache(await store.ListDomainResolutionsAsync(tunnelName));
            }
            finally
            {
                _warmStart.TrySetResult();
            }

            // Leave _knownGeneration null so the first poll reconciles geoip deltas; baseline the resolve epoch.
            _knownResolveEpoch = await ReadResolveEpochAsync();
            var initialListId = await store.GetActiveRoutingListIdAsync(tunnelName) ?? 0;
            lock (_lock)
            {
                _activeListId = initialListId;
            }

            // No timer-driven re-resolve (that periodically hammered every tracked domain and congested the
            // tunnel DNS). Active domains stay fresh via the on-demand DNS path; a full re-resolve runs ONLY
            // when a routing list actually changed (resolve-epoch bump). The poll just watches two cheap
            // signals: the list materialization generation and the resolve epoch.
            var pollInterval = TimeSpan.FromSeconds(Math.Clamp(Math.Min(refreshSeconds, 15), 1, 60));
            while (true)
            {
                await Task.Delay(pollInterval, ct);

                try
                {
                    var current = await store.GetActiveRoutingListMaterializationAsync(tunnelName);
                    if (current is not null && current.Generation != _knownGeneration)
                    {
                        lock (_lock)
                        {
                            _activeListId = current.ListId;
                        }

                        ReconcileStaticRoutes(current.Routes);
                        // Rebuild the matcher, seed newly added domains, and prune domains that left the lists.
                        _onGeoDomainsChanged?.Invoke(current.Domains, ct);
                        _knownGeneration = current.Generation;
                    }

                    var epoch = await ReadResolveEpochAsync();
                    if (epoch != _knownResolveEpoch)
                    {
                        _knownResolveEpoch = epoch;
                        await RefreshAsync();
                    }
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "geo cache signal poll failed for {Tunnel}", tunnelName);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Release the warm-start waiter so SeedRoutesAsync never hangs.
            _warmStart.TrySetResult();
        }
        catch (Exception ex)
        {
            _warmStart.TrySetResult();
            logger.LogError(ex, "domain tracker for {Tunnel} stopped", tunnelName);
        }
    }

    /// <summary>
    /// Warm-starts from the DB cache: installs /32 routes and advertises the whole set in one allowed-ips replace.
    /// </summary>
    public void SeedFromCache(IReadOnlyList<DomainResolution> cached)
    {
        if (cached.Count == 0)
        {
            return;
        }

        lock (_lock)
        {
            var index = EnsureIndex();
            if (index is null)
            {
                return;
            }

            foreach (var resolution in cached)
            {
                var key = resolution.Domain.TrimEnd('.').ToLowerInvariant();
                var effective = stripV6 ? resolution.Ips.Where(ip => !ip.Contains(':')) : resolution.Ips;
                var set = new HashSet<string>(effective);
                foreach (var ip in set)
                {
                    routes.AddTunnelRoute(IPAddress.Parse(ip), index.Value);
                }

                _current[key] = set;
            }

            uapi.SetAllowedIps(tunnelName, peerPublicKey, BuildAllowedIps());
        }
    }

    /// <summary>
    /// Routes per-app discovered remote IPs through the tunnel; only newly seen IPs install a route.
    /// </summary>
    public bool UpdateAppIps(IReadOnlyList<string> ips)
    {
        lock (_lock)
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

    private async Task<long> ReadResolveEpochAsync()
    {
        var value = await store.GetSettingAsync("geo-resolve-epoch");
        return long.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var epoch) ? epoch : 0;
    }

    private async Task RefreshAsync()
    {
        List<string> domains;
        lock (_lock)
        {
            domains = [.. _current.Keys];
        }

        var failed = 0;
        foreach (var domain in domains)
        {
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(domain);
                var ips = addresses
                    .Where(a => a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                    .Select(a => a.ToString())
                    .ToList();
                if (ips.Count > 0)
                {
                    // Add-only: refresh brings in new IPs but never drops a domain's live ones (a partial
                    // answer must not blackhole an active flow). Departed domains are pruned via the matcher.
                    Add(domain, ips);
                }
                else
                {
                    failed++;
                }
            }
            catch (Exception)
            {
                failed++;
            }
        }

        // One Warn per cycle when tracked domains could not be re-resolved; cached IPs are kept.
        if (failed > 0)
        {
            logger.LogWarning("re-resolve for {Tunnel}: {Failed}/{Total} tracked domain(s) unreachable", tunnelName, failed, domains.Count);
        }
    }

    private uint? EnsureIndex()
    {
        _interfaceIndex ??= routes.FindInterfaceIndex(tunnelName);
        return _interfaceIndex;
    }
}
