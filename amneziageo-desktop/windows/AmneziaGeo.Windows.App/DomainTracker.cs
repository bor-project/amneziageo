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
    // App-discovered remote IPs, unioned into allowed-ips so the watcher and DNS path share one authority.
    private readonly HashSet<string> _appIps = [];

    // Static geoip CIDRs captured at connect; add-only live, since removing an engine route mid-session blackholes.
    private readonly HashSet<string> _staticRoutes = new(staticRoutes, StringComparer.Ordinal);

    // Baselines for the poll signals: list materialization generation and global resolve epoch.
    private long? _knownGeneration;
    private long _knownResolveEpoch;

    // Live geo-domain sink; rebuilt on materialization generation change so a source refresh takes effect without reconnect.
    private volatile Action<IReadOnlyList<GeoDomain>, CancellationToken>? _onGeoDomainsChanged;

    private uint? _interfaceIndex;
    private readonly TaskCompletionSource _warmStart = new(TaskCreationOptions.RunContinuationsAsynchronously);

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
    /// Applies a domain's resolved IPs and persists.
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
            // IPv4-only tunnel: never route IPv6 (no transit).
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

            // Remove the /32 only when no other domain or _appIps still references the IP, so routes and allowed-ips stay in sync.
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
                routes.RemoveTunnelRoutes(stale!, index.Value);
            }

            _current[key] = fresh;

            logger.LogInformation("resolved {Domain} -> {Ips}", key, string.Join(", ", fresh));

            if (RouteLog.Enabled)
            {
                RouteLog.Note($"resolve {key} -> [{string.Join(",", fresh)}] (+{addedCidrs.Count} route(s), -{stale?.Count ?? 0})");
            }

            // Advertise only the newly added IPs incrementally so route-before-answer stays O(new).
            if (removedAny)
            {
                uapi.SetAllowedIps(tunnelName, peerPublicKey, BuildAllowedIps());
            }
            else
            {
                uapi.AddAllowedIps(tunnelName, peerPublicKey, addedCidrs);
            }

            // Fire-and-forget: persistence must not block the live DNS answer path.
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
    /// Applies cached resolutions (warm start), then re-resolves periodically.
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

            // Poll signals often so a user refresh takes effect promptly; full re-resolve runs every refreshSeconds.
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
                        _onGeoDomainsChanged?.Invoke(current.Domains, ct);
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
                // v4-only tunnel: never route IPv6.
                if (stripV6 && cidr.Contains(':'))
                {
                    continue;
                }

                if (!_staticRoutes.Add(cidr))
                {
                    continue;
                }

                // Advertise the range only once its route is installed, so routes and allowed-ips stay in sync.
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
