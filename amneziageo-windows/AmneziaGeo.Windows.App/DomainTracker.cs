using System.Net;
using System.Net.Sockets;
using AmneziaGeo.Decl;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Resolves tunneled domains to IPs, persists them, and keeps them fresh by re-resolving.
/// </summary>
internal sealed class DomainTracker(string tunnelName, string peerPublicKey, IStateStore store)
{
    private const int RefreshSeconds = 15;
    private readonly object _lock = new();
    private readonly Dictionary<string, HashSet<string>> _current = [];
    private GeoActivator? _activator;

    /// <summary>
    /// Applies a domain's resolved IPs: routes new ones, drops stale ones, and persists the change.
    /// </summary>
    public void Update(string domain, IReadOnlyList<string> ips)
    {
        lock (_lock)
        {
            var activator = EnsureActivator();
            if (activator is null)
            {
                return;
            }

            var key = domain.TrimEnd('.').ToLowerInvariant();
            var fresh = new HashSet<string>(ips);
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
                    activator.TunnelIp(IPAddress.Parse(ip));
                }
            }

            foreach (var ip in old)
            {
                if (!fresh.Contains(ip))
                {
                    RouteManager.RemoveTunnelRoute(IPAddress.Parse(ip));
                }
            }

            _current[key] = fresh;
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
            while (RouteManager.FindInterfaceIndex(tunnelName) is null)
            {
                await Task.Delay(500);
            }

            foreach (var resolution in await store.ListDomainResolutionsAsync(tunnelName))
            {
                Update(resolution.Domain, resolution.Ips);
            }

            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(RefreshSeconds));
                await RefreshAsync();
            }
        }
        catch (Exception)
        {
        }
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
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
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

    private GeoActivator? EnsureActivator()
    {
        if (_activator is not null)
        {
            return _activator;
        }

        var index = RouteManager.FindInterfaceIndex(tunnelName);
        if (index is null)
        {
            return null;
        }

        _activator = new GeoActivator(tunnelName, peerPublicKey, index.Value);
        return _activator;
    }
}
