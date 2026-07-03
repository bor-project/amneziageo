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
    int refreshSeconds,
    bool stripV6)
{
    private readonly object _lock = new();
    private readonly Dictionary<string, HashSet<string>> _current = [];
    // Remote IPs discovered by the per-app route watcher (#68). Kept separate from the domain map so the
    // re-resolve loop never touches them (they have no domain), but unioned into the allowed-ips so they
    // share the single authority - otherwise the app watcher and the DNS path would clobber each other's
    // SetAllowedIps. Accumulates for the life of the tunnel; capped so a chatty app cannot grow it without
    // bound.
    private readonly HashSet<string> _appIps = [];

    // The list-materialized static route set (geoip CIDRs, plus the tunnel resolver's /32s when domains are
    // tracked), captured at connect and grown live when a source refresh adds ranges (#83). Add-only: a range
    // dropped from the list keeps its route until reconnect, since removing an engine-installed route live
    // would blackhole traffic to it (the peer would still reject a since-removed allowed-ip). Guarded by _lock.
    private readonly HashSet<string> _staticRoutes = new(staticRoutes, StringComparer.Ordinal);

    // Baselines for the signals a running tunnel polls over the shared store: the active routing list's
    // materialization generation (a change = re-apply geoip ranges) and the global resolve epoch (a change =
    // re-resolve all tracked domains now). Captured once the tracker starts, after the warm-start cache load.
    private long? _knownGeneration;
    private long _knownResolveEpoch;

    private uint? _interfaceIndex;
    private readonly TaskCompletionSource _warmStart = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Completes once the DB-cache warm start has been applied. The eager route seeder awaits this and then
    /// resolves ONLY domains the cache did not already restore, so a reconnect with a warm cache issues no
    /// up-front DNS at all.
    /// </summary>
    public Task WarmStartCompleted => _warmStart.Task;

    /// <summary>True when a domain's resolution is already known (restored from cache or resolved before).</summary>
    public bool IsTracked(string domain)
    {
        var key = domain.TrimEnd('.').ToLowerInvariant();
        lock (_lock)
        {
            return _current.ContainsKey(key);
        }
    }

    /// <summary>
    /// Applies a domain's resolved IPs: routes new ones, drops stale ones, replaces allowed IPs, and persists.
    /// </summary>
    public void Update(string domain, IReadOnlyList<string> ips)
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
            // On an IPv4-only tunnel, never route IPv6: those addresses have no transit and only
            // make clients stall before falling back to IPv4.
            var effective = stripV6 ? ips.Where(ip => !ip.Contains(':')).ToList() : ips;
            var fresh = new HashSet<string>(effective);
            _current.TryGetValue(key, out var old);
            old ??= [];
            if (fresh.SetEquals(old))
            {
                logger.LogDebug("DIAG track {Domain} unchanged ({N} ips)", domain, fresh.Count);
                return;
            }

            var addedCidrs = new List<string>();
            foreach (var ip in fresh)
            {
                if (!old.Contains(ip))
                {
                    var parsed = IPAddress.Parse(ip);
                    var ok = routes.AddTunnelRoute(parsed, index.Value);
                    logger.LogDebug("DIAG track add {Ip}/32 -> ifIndex {Index} ok={Ok}", ip, index.Value, ok);
                    if (ok)
                    {
                        addedCidrs.Add(Cidr(parsed));
                    }
                }
            }

            // A resolved IP is frequently shared: CDN/anycast edges resolve for many hostnames, and the
            // per-app watcher (#68) pins IPs in _appIps. BuildAllowedIps() unions ALL of those, so dropping
            // a /32 route just because it left THIS domain's set - while another domain or _appIps still
            // advertises it in allowed-ips - desyncs routing from allowed-ips: the IP stays tunnel-eligible
            // but has no route, so its packets fall off the tunnel until a later refresh happens to re-add
            // it. Remove the route only when nothing else still needs the IP (#73).
            List<IPAddress>? stale = null;
            foreach (var ip in old)
            {
                if (!fresh.Contains(ip) && !IsStillReferenced(ip, key))
                {
                    (stale ??= []).Add(IPAddress.Parse(ip));
                }
            }

            var removedAny = stale is not null;
            if (removedAny)
            {
                // Delete all stale routes with a single forwarding-table read instead of one full-table
                // scan per IP - the per-IP path in a loop is O(stale * table) under this lock.
                routes.RemoveTunnelRoutes(stale!, index.Value);
            }

            _current[key] = fresh;

            // Resolves at Info (visible at the default "Обычный" level): the meaningful domain -> IPs event.
            // Logged only when the set actually changed (an unchanged re-resolve returned above), so a periodic
            // re-resolve that yields the same IPs never spams the log.
            logger.LogInformation("resolved {Domain} -> {Ips}", key, string.Join(", ", fresh));

            // The same story line for the routing log (off by default): the resolution and what it changed, so
            // a support engineer sees "domain -> ips" immediately above the /32 routes RouteManager then logs.
            // Guarded so the interpolation is skipped on the hot resolve path when the routing log is disabled.
            if (RouteLog.Enabled)
            {
                RouteLog.Note($"resolve {key} -> [{string.Join(",", fresh)}] (+{addedCidrs.Count} route(s), -{stale?.Count ?? 0})");
            }

            // A removal can only be expressed by replacing the whole peer set (the UAPI has no delete-one-
            // allowed-ip), so churn from the background re-resolve still pays the full O(total) push. But the
            // live DNS answer path only ever ADDS a freshly matched domain's IPs - advertise just those, in a
            // single incremental exchange, so route-before-answer stays O(new) instead of waiting on a
            // multi-thousand-entry replace that grows with every domain browsed (the cause of every newly
            // resolved domain hanging before its answer).
            if (removedAny)
            {
                uapi.SetAllowedIps(tunnelName, peerPublicKey, BuildAllowedIps());
            }
            else
            {
                uapi.AddAllowedIps(tunnelName, peerPublicKey, addedCidrs);
            }

            // Persistence is only a warm-start cache; it must not block the caller, which on the live
            // DNS path now runs before the answer is sent (route-before-answer). A synchronous SQLite
            // write here would add disk latency to every freshly resolved domain and serialize route
            // installs under the lock. Fire-and-forget; a refresh self-heals any out-of-order write.
            var snapshot = new DomainResolution(key, [.. fresh]);
            _ = Task.Run(() => PersistAsync(snapshot));
        }
    }

    private async Task PersistAsync(DomainResolution resolution)
    {
        try
        {
            await store.SaveDomainResolutionAsync(tunnelName, resolution);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "persist of {Domain} resolution failed", resolution.Domain);
        }
    }

    /// <summary>
    /// Waits for the tunnel adapter, applies the cached resolutions in one push (warm start), then
    /// re-resolves periodically. Eager up-front resolution of the whole rule set is intentionally NOT done
    /// here - the warm cache covers known domains, the live DNS path covers visited ones on demand, and
    /// <see cref="DnsProxy.SeedRoutesAsync"/> (after <see cref="WarmStartCompleted"/>) resolves only the rest.
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

            // Leave _knownGeneration null so the FIRST poll reconciles the geoip set against the live routing
            // list: if a source refresh re-materialized the list during the connect window (the projection
            // captured at connect held the old set), ApplyStaticAdditions adds the delta on that first poll;
            // when nothing changed it is a no-op (the set already matches), so connect stays behaviour-neutral.
            // The resolve epoch IS baselined to the current value, so an unchanged epoch does not force a
            // spurious re-resolve on the first poll.
            _knownResolveEpoch = await ReadResolveEpochAsync();

            // Poll the signals more often than a full re-resolve so a user "Обновить" (which bumps the resolve
            // epoch) takes effect promptly, without re-resolving every domain on each short poll. A full
            // periodic re-resolve still runs every refreshSeconds - domain IPs drift on their own TTLs.
            var pollInterval = TimeSpan.FromSeconds(Math.Clamp(Math.Min(refreshSeconds, 15), 1, 60));
            var fullRefresh = TimeSpan.FromSeconds(refreshSeconds);
            var sinceFullRefresh = TimeSpan.Zero;
            while (true)
            {
                await Task.Delay(pollInterval, ct);
                sinceFullRefresh += pollInterval;

                var forceResolve = false;
                try
                {
                    var current = await store.GetActiveRoutingListMaterializationAsync(tunnelName);
                    if (current is not null && current.Generation != _knownGeneration)
                    {
                        ApplyStaticAdditions(current.Routes);
                        _knownGeneration = current.Generation;
                    }

                    var epoch = await ReadResolveEpochAsync();
                    if (epoch != _knownResolveEpoch)
                    {
                        _knownResolveEpoch = epoch;
                        forceResolve = true;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "geo cache signal poll failed for {Tunnel}", tunnelName);
                }

                if (forceResolve || sinceFullRefresh >= fullRefresh)
                {
                    await RefreshAsync();
                    sinceFullRefresh = TimeSpan.Zero;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Tunnel torn down: stop the refresh loop with the session (the warm-start waiter must still
            // be released so DnsProxy.SeedRoutesAsync never hangs).
            _warmStart.TrySetResult();
        }
        catch (Exception ex)
        {
            _warmStart.TrySetResult();
            logger.LogError(ex, "domain tracker for {Tunnel} stopped", tunnelName);
        }
    }

    /// <summary>
    /// Warm-start application of the DB cache: installs the /32 route for every cached IP and advertises the
    /// whole set to the peer in a SINGLE allowed-ips replace - O(1) UAPI round-trips instead of one per
    /// cached domain - and populates <c>_current</c> so the on-demand path and the route seeder can tell
    /// which domains are already known. No-op when the cache is empty (the static set is already on the
    /// device from the engine's initial config).
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
    /// Routes a set of remote IPs discovered by the per-app route watcher (#68) through the tunnel: adds a
    /// /32 for each newly seen IP and folds them into the allowed-ips. Caller passes the IPs matched this
    /// poll; only previously unseen ones install a route, so it is cheap to call every tick. No-op until the
    /// tunnel adapter exists.
    /// </summary>
    public bool UpdateAppIps(IReadOnlyList<string> ips)
    {
        lock (_lock)
        {
            var index = EnsureIndex();
            if (index is null)
            {
                return false; // tunnel adapter not up yet - let the caller retry later
            }

            var addedCidrs = new List<string>();
            var allHandled = true;
            foreach (var ip in ips)
            {
                // v4-only tunnel: never route IPv6 (no transit). The watcher already enumerates IPv4 only,
                // but guard so a future v6 path cannot leak dead routes onto a stripV6 tunnel.
                if (stripV6 && ip.Contains(':'))
                {
                    continue;
                }

                if (_appIps.Contains(ip))
                {
                    continue; // already tracked
                }

                // Bound the set so a chatty app (many short-lived endpoints) cannot grow it without limit.
                if (_appIps.Count >= 8192)
                {
                    allHandled = false; // not recorded, so the caller does not mark it done
                    break;
                }

                // Advertise the IP in allowed-ips only once its /32 route is actually installed, so the
                // route set and allowed-ips never diverge (#73). AddTunnelRoute treats AlreadyExists as
                // success; a genuine failure leaves the IP unseen so the next poll retries it.
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
                    allHandled = false; // route add failed - allow a retry on the next event/poll
                }
            }

            // The app set only ever grows within a session, so advertise the new IPs incrementally rather
            // than re-pushing the whole peer set on every poll that discovers one (no-op when none were added).
            uapi.AddAllowedIps(tunnelName, peerPublicKey, addedCidrs);
            return allHandled;
        }
    }

    // True when an IP - other than via <paramref name="excludeKey"/>'s just-applied fresh set - is still
    // advertised in allowed-ips: held by another tracked domain or by the per-app watcher's set. Used to
    // keep the /32 route set in lockstep with BuildAllowedIps() so a shared IP is never left in allowed-ips
    // without a backing route (#73). Caller holds _lock.
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

    // Host CIDR for an address: /32 for IPv4, /128 for IPv6. Single source of truth for the prefix so the
    // incremental add path and the full BuildAllowedIps() replace never disagree on how an IP is advertised.
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

        // App-discovered IPs share the same allowed-ips authority (single SetAllowedIps owner).
        foreach (var ip in _appIps)
        {
            all.Add(Cidr(IPAddress.Parse(ip)));
        }

        return all;
    }

    /// <summary>
    /// Applies geoip ranges that appeared in the active routing list since connect (a source refresh grew a
    /// category): installs a tunnel route for each newly present CIDR and advertises it to the peer in one
    /// incremental exchange. Add-only - a range dropped from the list keeps its route until reconnect, so a
    /// stale range is over-tunnelled (harmless) rather than blackholed. No-op until the adapter is up.
    /// </summary>
    private void ApplyStaticAdditions(IReadOnlyList<string> freshRoutes)
    {
        lock (_lock)
        {
            var index = EnsureIndex();
            if (index is null)
            {
                return;
            }

            var addedCidrs = new List<string>();
            foreach (var cidr in freshRoutes)
            {
                // v4-only tunnel: never route IPv6 (no transit), matching the connect-time stripV6 filter.
                if (stripV6 && cidr.Contains(':'))
                {
                    continue;
                }

                if (!_staticRoutes.Add(cidr))
                {
                    continue; // already present
                }

                // Advertise the range only once its route is installed, so the route set and allowed-ips never
                // diverge (#73). A failed install is rolled back out of the set so a later poll retries it.
                if (routes.AddTunnelCidr(cidr, index.Value))
                {
                    addedCidrs.Add(cidr);
                }
                else
                {
                    _staticRoutes.Remove(cidr);
                }
            }

            if (addedCidrs.Count > 0)
            {
                logger.LogInformation("geo cache: applied {Count} new range(s) live to {Tunnel}", addedCidrs.Count, tunnelName);
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
                    Update(domain, ips);
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

        // One Warn per cycle (not per domain) when tracked domains could not be re-resolved - couldn't reach
        // DNS ("недостучались"). The previously resolved IPs are kept, so this is a heads-up, not a failure.
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
