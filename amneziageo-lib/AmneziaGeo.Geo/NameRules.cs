using System.Text.RegularExpressions;
using AmneziaGeo.Decl;
using AmneziaGeo.Routing;

namespace AmneziaGeo.Geo;

/// <summary>
/// Decides a name by the narrowest rule of a list that holds it: a host the list names outright before a category,
/// then the exact name, the longest suffix, a keyword and an expression; on the same width the block, then the direct
/// rule, then the tunnel.
/// </summary>
public sealed class NameRules
{
    private readonly Dictionary<string, RouteVerdict> _hosts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RouteVerdict> _full = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RouteVerdict> _domain = new(StringComparer.Ordinal);
    private readonly List<(string Value, RouteVerdict Verdict)> _plain = [];
    private readonly List<(Regex Pattern, RouteVerdict Verdict)> _regex = [];

    /// <summary>
    /// ctor
    /// </summary>
    public NameRules(IReadOnlyList<GeoDomain> proxy, IReadOnlyList<GeoDomain> direct, IReadOnlyList<GeoDomain> block)
    {
        Add(proxy, RouteVerdict.Proxy);
        Add(direct, RouteVerdict.Direct);
        Add(block, RouteVerdict.Block);
        _plain.Sort(static (a, b) => a.Value.Length != b.Value.Length ? b.Value.Length.CompareTo(a.Value.Length) : Rank(b.Verdict).CompareTo(Rank(a.Verdict)));
        _regex.Sort(static (a, b) => Rank(b.Verdict).CompareTo(Rank(a.Verdict)));
    }

    /// <summary>
    /// The rules of a list's names.
    /// </summary>
    public static NameRules Of(RoutingList list) => new(list.Domains, list.DirectDomains, list.BlockDomains);

    /// <summary>
    /// Whether no rule names any name.
    /// </summary>
    public bool IsEmpty => _hosts.Count == 0 && _full.Count == 0 && _domain.Count == 0 && _plain.Count == 0 && _regex.Count == 0;

    /// <summary>
    /// Where the rules send the name; None where no rule holds it.
    /// </summary>
    public RouteVerdict Verdict(string name) => Match(name)?.Verdict ?? RouteVerdict.None;

    /// <summary>
    /// The rule that decides the name, or null.
    /// </summary>
    public NameMatch? Match(string name)
    {
        var host = Normalize(name);
        if (host.Length == 0)
        {
            return null;
        }

        if (Suffix(_hosts, host) is { } named)
        {
            return named with { Named = true };
        }

        if (_full.TryGetValue(host, out var exact))
        {
            return new NameMatch(exact, GeoDomainKind.Full, host, false);
        }

        if (Suffix(_domain, host) is { } suffix)
        {
            return suffix;
        }

        foreach (var (value, verdict) in _plain)
        {
            if (host.Contains(value, StringComparison.Ordinal))
            {
                return new NameMatch(verdict, GeoDomainKind.Plain, value, false);
            }
        }

        foreach (var (pattern, verdict) in _regex)
        {
            if (pattern.IsMatch(host))
            {
                return new NameMatch(verdict, GeoDomainKind.Regex, pattern.ToString(), false);
            }
        }

        return null;
    }

    // The longest suffix of the host a rule holds, the host itself first.
    private static NameMatch? Suffix(Dictionary<string, RouteVerdict> suffixes, string host)
    {
        if (suffixes.Count == 0)
        {
            return null;
        }

        if (suffixes.TryGetValue(host, out var whole))
        {
            return new NameMatch(whole, GeoDomainKind.Domain, host, false);
        }

        for (var i = 0; i < host.Length; i++)
        {
            if (host[i] == '.' && suffixes.TryGetValue(host[(i + 1)..], out var verdict))
            {
                return new NameMatch(verdict, GeoDomainKind.Domain, host[(i + 1)..], false);
            }
        }

        return null;
    }

    private void Add(IReadOnlyList<GeoDomain> domains, RouteVerdict verdict)
    {
        foreach (var entry in domains)
        {
            switch (entry.Kind)
            {
                case GeoDomainKind.Domain when entry.Named:
                    Keep(_hosts, Normalize(entry.Value), verdict);
                    break;
                case GeoDomainKind.Full:
                    Keep(_full, Normalize(entry.Value), verdict);
                    break;
                case GeoDomainKind.Domain:
                    Keep(_domain, Normalize(entry.Value), verdict);
                    break;
                case GeoDomainKind.Plain:
                    _plain.Add((entry.Value.ToLowerInvariant(), verdict));
                    break;
                case GeoDomainKind.Regex:
                    try
                    {
                        _regex.Add((new Regex(entry.Value), verdict));
                    }
                    catch (ArgumentException)
                    {
                    }

                    break;
            }
        }
    }

    // Keeps the stronger of two rules on one key.
    private static void Keep(Dictionary<string, RouteVerdict> map, string key, RouteVerdict verdict)
    {
        if (key.Length == 0)
        {
            return;
        }

        if (!map.TryGetValue(key, out var held) || Rank(verdict) > Rank(held))
        {
            map[key] = verdict;
        }
    }

    private static int Rank(RouteVerdict verdict) => verdict switch
    {
        RouteVerdict.Block => 3,
        RouteVerdict.Direct => 2,
        RouteVerdict.Proxy => 1,
        _ => 0,
    };

    private static string Normalize(string value) => value.Trim().TrimEnd('.').ToLowerInvariant();
}

/// <summary>
/// The rule that decided a name: where it sends it, its kind and value, and whether the list names the host outright.
/// </summary>
public readonly record struct NameMatch(RouteVerdict Verdict, GeoDomainKind Kind, string Value, bool Named);
