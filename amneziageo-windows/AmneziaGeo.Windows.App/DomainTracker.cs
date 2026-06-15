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
    private uint? _interfaceIndex;

    /// <summary>
    /// Applies a domain's resolved IPs: routes new ones, drops stale ones, replaces allowed IPs, and persists.
    /// </summary>
    public void Update(string domain, IReadOnlyList<string> ips)
    {
        lock (_lock)
        {
            var index = EnsureIndex();
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
                return;
            }

            foreach (var ip in fresh)
            {
                if (!old.Contains(ip))
                {
                    routes.AddTunnelRoute(IPAddress.Parse(ip), index.Value);
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
            store.SaveDomainResolutionAsync(tunnelName, new DomainResolution(key, [.. fresh])).GetAwaiter().GetResult();
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
