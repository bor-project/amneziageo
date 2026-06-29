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
    private uint? _interfaceIndex;

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
            var removedAny = false;
            foreach (var ip in old)
            {
                if (!fresh.Contains(ip) && !IsStillReferenced(ip, key))
                {
                    routes.RemoveTunnelRoute(IPAddress.Parse(ip), index.Value);
                    removedAny = true;
                }
            }

            _current[key] = fresh;

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
    /// Waits for the tunnel adapter, applies saved resolutions, then re-resolves periodically.
    /// </summary>
    public async Task RunAsync()
    {
        try
        {
            while (routes.FindInterfaceIndex(tunnelName) is null)
            {
                await Task.Delay(500);
            }

            foreach (var resolution in await store.ListDomainResolutionsAsync(tunnelName))
            {
                Update(resolution.Domain, resolution.Ips);
            }

            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(refreshSeconds));
                await RefreshAsync();
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "domain tracker for {Tunnel} stopped", tunnelName);
        }
    }

    /// <summary>
    /// Routes a set of remote IPs discovered by the per-app route watcher (#68) through the tunnel: adds a
    /// /32 for each newly seen IP and folds them into the allowed-ips. Caller passes the IPs matched this
    /// poll; only previously unseen ones install a route, so it is cheap to call every tick. No-op until the
    /// tunnel adapter exists.
    /// </summary>
    public void UpdateAppIps(IReadOnlyList<string> ips)
    {
        lock (_lock)
        {
            var index = EnsureIndex();
            if (index is null)
            {
                return;
            }

            var addedCidrs = new List<string>();
            foreach (var ip in ips)
            {
                // v4-only tunnel: never route IPv6 (no transit). The watcher already enumerates IPv4 only,
                // but guard so a future v6 path cannot leak dead routes onto a stripV6 tunnel.
                if (stripV6 && ip.Contains(':'))
                {
                    continue;
                }

                // Bound the set so a chatty app (many short-lived endpoints) cannot grow it without limit.
                if (_appIps.Count >= 8192)
                {
                    break;
                }

                if (!_appIps.Contains(ip))
                {
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
                }
            }

            // The app set only ever grows within a session, so advertise the new IPs incrementally rather
            // than re-pushing the whole peer set on every poll that discovers one (no-op when none were added).
            uapi.AddAllowedIps(tunnelName, peerPublicKey, addedCidrs);
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
        var all = new List<string>(staticRoutes);
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

    private async Task RefreshAsync()
    {
        List<string> domains;
        lock (_lock)
        {
            domains = [.. _current.Keys];
        }

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
            }
            catch (Exception)
            {
            }
        }
    }

    private uint? EnsureIndex()
    {
        _interfaceIndex ??= routes.FindInterfaceIndex(tunnelName);
        return _interfaceIndex;
    }
}
