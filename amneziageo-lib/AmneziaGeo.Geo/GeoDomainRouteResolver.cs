using System.Net;
using System.Net.Sockets;
using AmneziaGeo.Decl;

namespace AmneziaGeo.Geo;

/// <summary>
/// Resolves geosite domains to host routes so an IP-only tunnel (Android) can carry them.
/// </summary>
public sealed class GeoDomainRouteResolver
{
    private readonly int _maxParallel;
    private readonly TimeSpan _perLookupTimeout;

    /// <summary>
    /// ctor
    /// </summary>
    public GeoDomainRouteResolver(int maxParallel = 16, TimeSpan? perLookupTimeout = null)
    {
        _maxParallel = maxParallel < 1 ? 1 : maxParallel;
        _perLookupTimeout = perLookupTimeout ?? TimeSpan.FromSeconds(3);
    }

    /// <summary>
    /// Resolves suffix and exact domains to /32 host routes; keyword and regex kinds have no host form and are skipped.
    /// Returns whatever resolved before the token was cancelled.
    /// </summary>
    public async Task<IReadOnlyList<string>> ResolveAsync(IReadOnlyList<GeoDomain> domains, CancellationToken ct = default)
    {
        var hosts = CollectHosts(domains);
        if (hosts.Count == 0)
        {
            return [];
        }

        var routes = new HashSet<string>(StringComparer.Ordinal);
        var gate = new SemaphoreSlim(_maxParallel);
        var tasks = new List<Task>(hosts.Count);
        foreach (var host in hosts)
        {
            tasks.Add(ResolveOneAsync(host, gate, routes, ct));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return [.. routes];
    }

    // The resolvable hostnames: suffix and exact domains, deduped; keyword, regex, and wildcard entries are dropped.
    private static HashSet<string> CollectHosts(IReadOnlyList<GeoDomain> domains)
    {
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var domain in domains)
        {
            if (domain.Kind is not (GeoDomainKind.Domain or GeoDomainKind.Full))
            {
                continue;
            }

            var host = domain.Value.Trim().Trim('.');
            if (IsHostname(host))
            {
                hosts.Add(host);
            }
        }

        return hosts;
    }

    private async Task ResolveOneAsync(string host, SemaphoreSlim gate, HashSet<string> routes, CancellationToken ct)
    {
        try
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            if (ct.IsCancellationRequested)
            {
                return;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_perLookupTimeout);
            var addresses = await Dns.GetHostAddressesAsync(host, timeout.Token).ConfigureAwait(false);
            foreach (var address in addresses)
            {
                if (address.AddressFamily == AddressFamily.InterNetwork)
                {
                    lock (routes)
                    {
                        routes.Add($"{address}/32");
                    }
                }
            }
        }
        catch (Exception)
        {
        }
        finally
        {
            gate.Release();
        }
    }

    // A resolvable hostname: has a dot, no wildcard or path separators.
    private static bool IsHostname(string value) =>
        value.Length > 0 && value.IndexOf('.') > 0 && value.IndexOf('*') < 0 && value.IndexOf('/') < 0;
}
