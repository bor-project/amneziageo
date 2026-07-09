using System.Linq;
using System.Net;
using System.Net.Sockets;
using AmneziaGeo.Decl;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Pins geo-source hosts to the physical gateway so their downloads bypass the tunnel when direct delivery works.
/// </summary>
internal sealed class DownloadRouteOptimizer(IStateStore store, RouteManager routes, ILogger<DownloadRouteOptimizer> logger)
{
    private static readonly TimeSpan _probeTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Pins reachable geo-source hosts direct and returns the pinned addresses.
    /// </summary>
    public async Task<IReadOnlyList<IPAddress>> ApplyAsync(string name, CancellationToken ct)
    {
        var hosts = await ResolveHostsAsync(ct);
        var pinned = new List<IPAddress>();
        foreach (var ip in hosts)
        {
            if (!routes.AddEndpointExclusion(name, ip))
            {
                continue;
            }

            if (await ReachableAsync(ip, ct))
            {
                pinned.Add(ip);
            }
            else
            {
                routes.RemoveEndpointExclusion(name, ip);
            }
        }

        logger.LogInformation("download routing: {Pinned}/{Total} geo hosts pinned direct", pinned.Count, hosts.Count);
        return pinned;
    }

    /// <summary>
    /// Removes the pinned host routes.
    /// </summary>
    public void Revert(string name, IReadOnlyList<IPAddress> pinned)
    {
        foreach (var ip in pinned)
        {
            routes.RemoveEndpointExclusion(name, ip);
        }
    }

    private async Task<IReadOnlyList<IPAddress>> ResolveHostsAsync(CancellationToken ct)
    {
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in await store.ListGeoSourcesAsync(ct))
        {
            if (Uri.TryCreate(source.Url, UriKind.Absolute, out var uri) && !IPAddress.TryParse(uri.Host, out _))
            {
                hosts.Add(uri.Host);
            }
        }

        var ips = new List<IPAddress>();
        var seen = new HashSet<IPAddress>();
        foreach (var host in hosts)
        {
            foreach (var ip in (await TryResolveAsync(host, ct)).Where(a => a.AddressFamily == AddressFamily.InterNetwork))
            {
                if (seen.Add(ip))
                {
                    ips.Add(ip);
                }
            }
        }

        return ips;
    }

    private async Task<IReadOnlyList<IPAddress>> TryResolveAsync(string host, CancellationToken ct)
    {
        try
        {
            return await Dns.GetHostAddressesAsync(host, ct);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            logger.LogWarning(ex, "download routing: resolve {Host} failed", host);
            return [];
        }
    }

    private static async Task<bool> ReachableAsync(IPAddress ip, CancellationToken ct)
    {
        using var probe = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_probeTimeout);
        try
        {
            await probe.ConnectAsync(ip, 443, timeout.Token);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return false;
        }
    }
}
