using System.Net;
using System.Net.Sockets;

namespace AmneziaGeo.Linux.App;

/// <summary>
/// The VPNs of other programs beside the tunnel: the resolver file they rewrite, the names under their suffixes, and
/// the servers the connections of NetworkManager dial.
/// </summary>
internal sealed class OtherVpns(IReadOnlyList<IPAddress> lanResolvers, AgentLog log)
{
    // How often the resolver file is looked at.
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(2);

    // How long the bring-up waits for the names of the servers.
    private static readonly TimeSpan ResolveBudget = TimeSpan.FromSeconds(3);

    private const string ProfilesFolder = "/etc/NetworkManager/system-connections";

    // The host routes to the servers this laid.
    private readonly List<string> _laid = [];
    private IReadOnlyList<IPAddress> _peerServers = [];
    // The resolver file as it was read last, so a rewrite is told from a poll.
    private string _seen = string.Empty;

    /// <summary>
    /// Watches the resolver file: what it named before the agent took it and what another program points it at later
    /// answers the names under its domains, and the file points at the agent again. The resolvers pppd took from its
    /// peer join the network's.
    /// </summary>
    public async Task WatchResolverAsync(DnsRouter router, CancellationToken ct)
    {
        try
        {
            var (saved, savedDomains) = ResolvConf.ReadSaved();
            if (Take(router, saved, savedDomains))
            {
                log.Info("dns", $"the system resolver named {string.Join(",", saved)} for {string.Join(", ", savedDomains)} before the agent took it; the names under them keep going there");
            }

            _seen = ResolvConf.Stamp();
            while (true)
            {
                await Task.Delay(Poll, ct).ConfigureAwait(false);
                FollowPeer(router);
                var stamp = ResolvConf.Stamp();
                if (stamp == _seen)
                {
                    continue;
                }

                _seen = stamp;
                var (servers, domains) = ResolvConf.Parse(ResolvConf.ReadText());
                if (ResolvConf.PointsAt(DnsRouter.Listen))
                {
                    // Put back the way the agent left it: the resolvers of the program that rewrote it leave with it.
                    if (Take(router, servers, domains) || router.UpdateSuffixPools([]))
                    {
                        log.Info("dns", "the system resolver was put back; the names of the other program go the usual way again");
                    }

                    continue;
                }

                var taken = Take(router, servers, domains);
                if (!ResolvConf.Repoint(DnsRouter.Listen, log))
                {
                    continue;
                }

                _seen = ResolvConf.Stamp();
                var where = domains.Count > 0 ? $" and the names under {string.Join(", ", domains)} go to them" : string.Empty;
                log.Info("dns", servers.Count > 0
                    ? $"another program pointed the system resolver at {string.Join(",", servers)}; lookups go to the agent again{where}{(taken ? string.Empty : " as before")}"
                    : "another program rewrote the system resolver without a nameserver; lookups go to the agent again");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            log.Error("dns", "watching the system resolver failed", ex);
        }
    }

    // Hands the router the resolvers a file names, for the names under its domains and as the network's; returns
    // whether the pools of the domains moved.
    private bool Take(DnsRouter router, IReadOnlyList<IPAddress> servers, IReadOnlyList<string> domains)
    {
        if (servers.Count == 0)
        {
            return router.UpdateSuffixPools([]);
        }

        var foreign = servers.Where(server => !lanResolvers.Contains(server)).ToList();
        var answering = foreign.Count > 0 ? foreign : servers;
        router.UpdateLan([.. servers, .. lanResolvers.Where(server => !servers.Contains(server))]);
        return router.UpdateSuffixPools([.. domains.Select(domain => (domain, (IReadOnlyList<IPAddress>)answering))]);
    }

    /// <summary>
    /// Lays a host route past the tunnel to every server of the connections, waiting a short while for their names; a
    /// route that stands already is left as it is.
    /// </summary>
    public async Task LayServerRoutesAsync(string? via, string? dev, CancellationToken ct)
    {
        if (via is null || dev is null)
        {
            return;
        }

        var servers = Servers();
        if (servers.Count == 0)
        {
            return;
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(ResolveBudget);
        try
        {
            foreach (var server in servers)
            {
                foreach (var address in await ResolveAsync(server, budget.Token).ConfigureAwait(false))
                {
                    var laid = await Shell.RunAsync("ip", budget.Token, "route", "add", $"{address}/32", "via", via, "dev", dev).ConfigureAwait(false);
                    if (laid.ExitCode == 0)
                    {
                        _laid.Add(address.ToString());
                        log.Route($"{address} via {via} dev {dev} (the server of another VPN)");
                    }
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            log.Debug("route", "the servers of the other VPNs are still being looked up; the tunnel comes up without waiting for them");
        }
    }

    /// <summary>
    /// Takes back the routes to the servers this laid.
    /// </summary>
    public async Task RemoveServerRoutesAsync()
    {
        foreach (var address in _laid)
        {
            await Shell.RunAsync("ip", CancellationToken.None, "route", "del", $"{address}/32").ConfigureAwait(false);
        }

        _laid.Clear();
    }

    /// <summary>
    /// The servers the VPN connections of NetworkManager dial, by the profiles in its folder.
    /// </summary>
    public static IReadOnlyList<string> Servers()
    {
        var servers = new List<string>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(ProfilesFolder))
            {
                foreach (var server in ParseProfile(File.ReadAllText(file)))
                {
                    if (!servers.Contains(server, StringComparer.OrdinalIgnoreCase))
                    {
                        servers.Add(server);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return servers;
    }

    /// <summary>
    /// The servers a NetworkManager profile dials: the gateway or the remotes of its vpn section.
    /// </summary>
    internal static IReadOnlyList<string> ParseProfile(string text)
    {
        var servers = new List<string>();
        var vpn = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                vpn = line.Equals("[vpn]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            var split = line.IndexOf('=');
            if (!vpn || split < 0)
            {
                continue;
            }

            var key = line[..split].Trim();
            if (key is not ("gateway" or "remote"))
            {
                continue;
            }

            foreach (var entry in line[(split + 1)..].Split(','))
            {
                var host = entry.Trim().Split(' ')[0].Split(':')[0];
                if (host.Length > 0 && !servers.Contains(host, StringComparer.OrdinalIgnoreCase))
                {
                    servers.Add(host);
                }
            }
        }

        return servers;
    }

    // Hands the router the resolvers pppd took from its peer when they moved.
    private void FollowPeer(DnsRouter router)
    {
        var servers = ResolvConf.ReadPeerNameservers();
        if (servers.SequenceEqual(_peerServers))
        {
            return;
        }

        _peerServers = servers;
        router.UpdateLan([.. servers, .. lanResolvers.Where(server => !servers.Contains(server))]);
        if (servers.Count > 0)
        {
            log.Info("dns", $"pppd took {string.Join(",", servers)} from its peer; names the network does not answer are asked there first");
        }
    }

    private static async Task<IReadOnlyList<IPAddress>> ResolveAsync(string server, CancellationToken ct)
    {
        if (IPAddress.TryParse(server, out var literal))
        {
            return literal.AddressFamily == AddressFamily.InterNetwork ? [literal] : [];
        }

        try
        {
            return [.. (await Dns.GetHostAddressesAsync(server, AddressFamily.InterNetwork, ct).ConfigureAwait(false)).Distinct()];
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            return [];
        }
    }
}
