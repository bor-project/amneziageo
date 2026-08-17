using System.Globalization;
using System.Net;
using System.Net.Sockets;

using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;
using AmneziaGeo.Routing;

namespace AmneziaGeo.Geo;

/// <summary>
/// What the running tunnel holds for one address: the role its path amounts to right now, and how it is held.
/// </summary>
public sealed record HeldRoute(RoleToken Role, string Detail);

/// <summary>
/// What the platform lends the inspector: what its live tunnel holds for an address, and which addresses an
/// application is talking to right now. Both are optional - without them the answer comes from the rules alone.
/// </summary>
public sealed record TargetProbes(
    Func<IPAddress, HeldRoute?>? Held = null,
    Func<string, IReadOnlyList<string>>? AppAddresses = null);

/// <summary>
/// Answers "why does this address, name, application or category go where it goes". The active list decides it,
/// and the matchers here are the ones the tunnel itself routes by, so the answer is the routing, not a guess.
/// </summary>
public sealed class TargetInspector(RoutingList? list, bool split, AppScope apps = AppScope.None)
{
    // Port a reachability probe knocks on when the target names none.
    private const int ProbePort = 443;
    private const int ProbeTimeoutMs = 3_000;

    // Addresses reported per target; a name behind a CDN answers with dozens.
    private const int MaxAddresses = 8;

    /// <summary>
    /// Which bucket claims a target, and the entry that claimed it.
    /// </summary>
    public sealed record Claim(RoleToken Role, string Rule);

    /// <summary>
    /// Runs the check and returns the finished report.
    /// </summary>
    public async Task<TargetReport> InspectAsync(string target, string config, TargetProbes probes, CancellationToken ct)
    {
        var token = target.Trim();
        var kind = KindOf(token);
        var facts = new List<CheckFact> { Mode() };

        var findings = new TargetFindings(kind, split, list is not null, Apps: apps, AppCount: Named);
        findings = kind switch
        {
            CheckTargetKind.App => Application(token, facts, findings, probes),
            CheckTargetKind.Geo => Category(token, facts, findings),
            CheckTargetKind.Address => Addresses(token, [IPAddress.Parse(Host(token))], facts, findings, probes),
            _ => await NameAsync(token, facts, findings, probes, ct).ConfigureAwait(false),
        };

        if (kind is CheckTargetKind.Domain or CheckTargetKind.Address && findings.Role == RoleToken.Proxy && findings.Addresses > 0)
        {
            var reached = await ReachableAsync(token, facts, ct).ConfigureAwait(false);
            findings = findings with { Reachable = reached };
        }

        var (key, args) = TargetVerdict.Decide(findings, token);
        return new TargetReport(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), config, token, facts, key, args);
    }

    // How many applications the list names.
    private int Named => list?.Apps.Count ?? 0;

    // The row that opens every check: what the list does with the traffic as a whole. An exclusive app split
    // decides ahead of every rule, so it is what the mode says.
    private CheckFact Mode()
    {
        var name = list is null ? "no list" : list.Name;
        if (apps == AppScope.Exclusive)
        {
            return new CheckFact("mode", name, "apps",
                $"only the {Named.ToString(CultureInfo.InvariantCulture)} named application(s) ride the tunnel, the rest pass every rule by");
        }

        var added = apps == AppScope.Additive
            ? $", and the {Named.ToString(CultureInfo.InvariantCulture)} named application(s) ride it on top of that"
            : string.Empty;
        return new CheckFact("mode", name, split ? "split" : "full",
            (split ? "only what the list names rides the tunnel" : "everything rides the tunnel except the direct bucket") + added);
    }

    /// <summary>
    /// The bucket a name falls into, by the same matchers the resolver routes it by.
    /// </summary>
    public Claim ForDomain(string host)
    {
        if (list is null)
        {
            return new Claim(RoleToken.None, string.Empty);
        }

        if (new DomainMatcher(list.BlockDomains).Match(host) is { } blocked)
        {
            return new Claim(RoleToken.Block, Entry(blocked));
        }

        if (new DomainMatcher(list.DirectDomains).Match(host) is { } direct)
        {
            return new Claim(RoleToken.Direct, Entry(direct));
        }

        return new DomainMatcher(list.Domains).Match(host) is { } proxied
            ? new Claim(RoleToken.Proxy, Entry(proxied))
            : new Claim(RoleToken.None, string.Empty);
    }

    /// <summary>
    /// The bucket an address falls into, by the same ranges the routing cache decides by.
    /// </summary>
    public Claim ForAddress(IPAddress address)
    {
        if (list is null || !GeoIpRanges.TryToNumeric(address, out var value))
        {
            return new Claim(RoleToken.None, string.Empty);
        }

        foreach (var (role, routes) in new[]
        {
            (RoleToken.Block, list.BlockRoutes),
            (RoleToken.Direct, list.DirectRoutes),
            (RoleToken.Proxy, list.Routes),
        })
        {
            var ranges = GeoIpRanges.Build(routes);
            if (ranges.Contains(value))
            {
                return new Claim(role, Span(ranges, value));
            }
        }

        return new Claim(RoleToken.None, string.Empty);
    }

    // An application: the rule that covers it, then the addresses it is talking to and what the rules say about them.
    private TargetFindings Application(string token, List<CheckFact> facts, TargetFindings findings, TargetProbes probes)
    {
        var value = Host(token);
        var rule = AppRule(value);
        facts.Add(new CheckFact("app", value, rule.Length > 0 ? "listed" : "unlisted",
            rule.Length > 0 ? $"covered by \"{rule}\"" : "no app rule names it"));

        var live = probes.AppAddresses?.Invoke(value) ?? [];
        var unlisted = 0;
        foreach (var address in live.Take(MaxAddresses))
        {
            if (!IPAddress.TryParse(address, out var parsed))
            {
                continue;
            }

            var claim = ForAddress(parsed);
            if (claim.Role == RoleToken.None)
            {
                unlisted++;
            }

            facts.Add(Address(parsed, claim, probes));
        }

        if (live.Count > 0)
        {
            facts.Add(new CheckFact("app", "destinations", live.Count > MaxAddresses ? "capped" : "counted",
                $"{live.Count.ToString(CultureInfo.InvariantCulture)} live destination(s), {unlisted.ToString(CultureInfo.InvariantCulture)} covered by no rule"));
        }

        return findings with
        {
            AppRule = rule,
            MatchedRule = rule,
            Addresses = live.Count,
            Unlisted = unlisted,
            Role = rule.Length > 0 ? RoleToken.Proxy : RoleToken.None,
        };
    }

    // A geo category: whether the active list carries it, and in which bucket.
    private TargetFindings Category(string token, List<CheckFact> facts, TargetFindings findings)
    {
        foreach (var rule in list?.Rules ?? [])
        {
            if (!string.Equals(GeoConfigurator.Format(rule), token, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var role = Role(rule.Role);
            facts.Add(new CheckFact("rule", token, role.ToString().ToLowerInvariant(), $"carried by the list \"{list!.Name}\""));
            return findings with { Role = role, MatchedRule = token, Addresses = 1 };
        }

        facts.Add(new CheckFact("rule", token, "absent", "no list in force carries this category"));
        return findings with { Role = RoleToken.None };
    }

    // A name: what it resolves to, what the name itself matches, and what its addresses match.
    private async Task<TargetFindings> NameAsync(string token, List<CheckFact> facts, TargetFindings findings, TargetProbes probes, CancellationToken ct)
    {
        var host = Host(token);
        var claim = ForDomain(host);
        facts.Add(new CheckFact("rule", host, claim.Role.ToString().ToLowerInvariant(),
            claim.Rule.Length > 0 ? $"matched by \"{claim.Rule}\"" : "no domain rule matches the name"));

        var resolved = await ResolveAsync(host, ct).ConfigureAwait(false);
        if (resolved.Count == 0)
        {
            facts.Add(new CheckFact("resolve", host, "failed", "the resolver returned no address"));
            return findings with { Resolved = false, Role = claim.Role, MatchedRule = claim.Rule };
        }

        facts.Add(new CheckFact("resolve", host, "ok", string.Join(", ", resolved.Take(MaxAddresses))));
        return Addresses(token, resolved, facts, findings with { Role = claim.Role, MatchedRule = claim.Rule }, probes);
    }

    // Addresses: the bucket each falls into, and what the running tunnel holds for it.
    private TargetFindings Addresses(string token, IReadOnlyList<IPAddress> addresses, List<CheckFact> facts, TargetFindings findings, TargetProbes probes)
    {
        var role = findings.Role;
        var rule = findings.MatchedRule;
        var unlisted = 0;
        foreach (var address in addresses.Take(MaxAddresses))
        {
            var claim = ForAddress(address);
            facts.Add(Address(address, claim, probes));
            if (claim.Role == RoleToken.None)
            {
                unlisted++;
                continue;
            }

            // The name's own verdict wins where it exists; an address rule answers for the rest.
            if (role == RoleToken.None)
            {
                role = claim.Role;
                rule = claim.Rule;
            }
        }

        return findings with { Role = role, MatchedRule = rule, Addresses = addresses.Count, Unlisted = unlisted };
    }

    // One address row. What the running tunnel holds for it is the state, because a name can settle an address
    // the ranges would claim; the range it falls into is then the reason, not the answer.
    private static CheckFact Address(IPAddress address, Claim claim, TargetProbes probes)
    {
        var held = probes.Held?.Invoke(address);
        var reason = claim.Rule.Length > 0 ? $"in {claim.Rule}" : "no range covers it";
        var state = (held?.Role ?? claim.Role).ToString().ToLowerInvariant();
        return new CheckFact("address", address.ToString(), state,
            held is null ? reason : $"{held.Detail}; {reason}");
    }

    // Whether anything answers on the target's port; only asked where a rule already puts it in the tunnel.
    private static async Task<bool> ReachableAsync(string token, List<CheckFact> facts, CancellationToken ct)
    {
        var host = Host(token);
        var port = Port(token);
        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(ProbeTimeoutMs);
        try
        {
            await socket.ConnectAsync(host, port, deadline.Token).ConfigureAwait(false);
            facts.Add(new CheckFact("probe", $"tcp {port.ToString(CultureInfo.InvariantCulture)}", "ok", "the connection was accepted"));
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException)
        {
            facts.Add(new CheckFact("probe", $"tcp {port.ToString(CultureInfo.InvariantCulture)}", "bad", "nothing accepted the connection"));
            return false;
        }
    }

    private static async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken ct)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
            return [.. addresses.Where(one => one.AddressFamily == AddressFamily.InterNetwork)];
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException or OperationCanceledException)
        {
            return [];
        }
    }

    // The app rule that names a package or an image, "" when none does.
    private string AppRule(string value)
    {
        foreach (var rule in list?.Rules ?? [])
        {
            if (rule.Kind == GeoRuleKind.App && rule.Value.Contains(value, StringComparison.OrdinalIgnoreCase))
            {
                return GeoConfigurator.Format(rule);
            }
        }

        return string.Empty;
    }

    private static string KindOf(string token)
    {
        if (token.StartsWith("app:", StringComparison.OrdinalIgnoreCase)
            || token.StartsWith("pkg=", StringComparison.OrdinalIgnoreCase)
            || token.StartsWith("path=", StringComparison.OrdinalIgnoreCase))
        {
            return CheckTargetKind.App;
        }

        if (token.StartsWith("geosite:", StringComparison.OrdinalIgnoreCase)
            || token.StartsWith("geoip:", StringComparison.OrdinalIgnoreCase))
        {
            return CheckTargetKind.Geo;
        }

        return IPAddress.TryParse(Host(token), out _) ? CheckTargetKind.Address : CheckTargetKind.Domain;
    }

    // The token without its prefix and without its port.
    private static string Host(string token)
    {
        var value = token;
        foreach (var prefix in new[] { "app:", "pkg=", "path=", "dir=", "svc=", "name=" })
        {
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                value = value[prefix.Length..];
            }
        }

        var colon = value.LastIndexOf(':');
        return colon > 0 && int.TryParse(value[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out _)
            ? value[..colon]
            : value;
    }

    private static int Port(string token)
    {
        var colon = token.LastIndexOf(':');
        return colon > 0 && int.TryParse(token[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            ? port
            : ProbePort;
    }

    private static RoleToken Role(RouteRole role)
    {
        return role switch
        {
            RouteRole.Direct => RoleToken.Direct,
            RouteRole.Block => RoleToken.Block,
            _ => RoleToken.Proxy,
        };
    }

    private static string Entry(DomainMatcher.GeoMatch match)
    {
        return GeoConfigurator.FormatDomain(new GeoDomain(match.Kind, match.Value));
    }

    // The range that covers an address, as the list itself carries it.
    private static string Span(GeoIpRanges ranges, uint address)
    {
        foreach (var (start, end) in ranges.Spans)
        {
            if (address >= start && address <= end)
            {
                return $"{GeoIpRanges.Format(start)}-{GeoIpRanges.Format(end)}";
            }
        }

        return "a listed range";
    }
}
