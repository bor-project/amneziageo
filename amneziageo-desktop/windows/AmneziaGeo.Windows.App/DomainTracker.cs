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
            logger.LogInformation("DIAG track {Domain} ips=[{Ips}] index={Index} stripV6={StripV6}", domain, string.Join(",", ips), index, stripV6);
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
                logger.LogInformation("DIAG track {Domain} unchanged ({N} ips)", domain, fresh.Count);
                return;
            }

            foreach (var ip in fresh)
            {
                if (!old.Contains(ip))
                {
                    var ok = routes.AddTunnelRoute(IPAddress.Parse(ip), index.Value);
                    logger.LogInformation("DIAG track add {Ip}/32 -> ifIndex {Index} ok={Ok}", ip, index.Value, ok);
                }
            }

            foreach (var ip in old)
            {
                if (!fresh.Contains(ip))
                {
                    routes.RemoveTunnelRoute(IPAddress.Parse(ip), index.Value);
                }
            }

            _current[key] = fresh;
            uapi.SetAllowedIps(tunnelName, peerPublicKey, BuildAllowedIps());

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

            var added = false;
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

                if (_appIps.Add(ip))
                {
                    var ok = routes.AddTunnelRoute(IPAddress.Parse(ip), index.Value);
                    logger.LogInformation("DIAG app route add {Ip}/32 -> ifIndex {Index} ok={Ok}", ip, index.Value, ok);
                    added = true;
                }
            }

            if (added)
            {
                uapi.SetAllowedIps(tunnelName, peerPublicKey, BuildAllowedIps());
            }
        }
    }

    private List<string> BuildAllowedIps()
    {
        var all = new List<string>(staticRoutes);
        foreach (var set in _current.Values)
        {
            foreach (var ip in set)
            {
                var prefix = IPAddress.Parse(ip).AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;
                all.Add($"{ip}/{prefix}");
            }
        }

        // App-discovered IPs share the same allowed-ips authority (single SetAllowedIps owner).
        foreach (var ip in _appIps)
        {
            var prefix = IPAddress.Parse(ip).AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;
            all.Add($"{ip}/{prefix}");
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
