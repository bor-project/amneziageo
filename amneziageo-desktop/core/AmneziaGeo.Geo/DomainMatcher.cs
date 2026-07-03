using System.Text.RegularExpressions;
using AmneziaGeo.Decl;

namespace AmneziaGeo.Geo;

/// <summary>
/// Decides whether a domain should be routed through the tunnel. Built ONCE from the materialized geo
/// domain set and reused for every DNS query: exact ("full") and suffix ("domain") rules are indexed in
/// hash sets for O(1)/O(labels) lookup, substring ("plain") values are pre-lower-cased, and regex rules
/// are pre-compiled - so a query costs no per-entry allocation and no regex recompilation, unlike the old
/// per-query linear scan that re-lower-cased every entry and re-parsed every pattern through the 15-slot
/// static Regex cache.
/// </summary>
public sealed class DomainMatcher
{
    private readonly HashSet<string> _full = new(StringComparer.Ordinal);
    private readonly HashSet<string> _domain = new(StringComparer.Ordinal);
    private readonly List<string> _plain = [];
    private readonly List<Regex> _regex = [];

    /// <summary>
    /// Builds the matcher index. Each value is normalized once (trailing dot trimmed, lower-cased); each
    /// regex is compiled once. A malformed regex pattern is skipped rather than failing the whole tunnel
    /// (the old code would have thrown on every query that reached it).
    /// </summary>
    public DomainMatcher(IReadOnlyList<GeoDomain> domains)
    {
        foreach (var entry in domains)
        {
            switch (entry.Kind)
            {
                case GeoDomainKind.Full:
                    _full.Add(Normalize(entry.Value));
                    break;
                case GeoDomainKind.Domain:
                    _domain.Add(Normalize(entry.Value));
                    break;
                case GeoDomainKind.Plain:
                    _plain.Add(entry.Value.ToLowerInvariant());
                    break;
                case GeoDomainKind.Regex:
                    try
                    {
                        _regex.Add(new Regex(entry.Value));
                    }
                    catch (ArgumentException)
                    {
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// Returns true when the domain matches any configured geo rule. Delegates to <see cref="Match"/> so
    /// there is a single matching implementation; <see cref="GeoMatch"/> is a stack-only struct, so the
    /// common (bool) path allocates nothing beyond the one lower-cased host that matching already needs.
    /// </summary>
    public bool IsTunneled(string domain) => Match(domain) is not null;

    /// <summary>
    /// Returns the geo rule that matches the domain - its kind and the value it matched on - or null when
    /// nothing matches, so a caller (e.g. the routing log) can report WHY a name was tunneled. Same order
    /// and semantics as before: full (exact), then exact/suffix domain, then substring plain, then regex.
    /// </summary>
    public GeoMatch? Match(string domain)
    {
        var host = domain.TrimEnd('.').ToLowerInvariant();
        if (host.Length == 0)
        {
            return null;
        }

        // Full: exact host. Domain: exact host, or any parent label-boundary suffix ("x.example.com"
        // matches "example.com") - the same semantics as host == value || host.EndsWith("." + value).
        if (_full.Contains(host))
        {
            return new GeoMatch(GeoDomainKind.Full, host);
        }

        if (_domain.Contains(host))
        {
            return new GeoMatch(GeoDomainKind.Domain, host);
        }

        if (_domain.Count > 0)
        {
            for (var i = 0; i < host.Length; i++)
            {
                if (host[i] == '.' && _domain.Contains(host[(i + 1)..]))
                {
                    return new GeoMatch(GeoDomainKind.Domain, host[(i + 1)..]);
                }
            }
        }

        foreach (var value in _plain)
        {
            if (host.Contains(value, StringComparison.Ordinal))
            {
                return new GeoMatch(GeoDomainKind.Plain, value);
            }
        }

        foreach (var regex in _regex)
        {
            if (regex.IsMatch(host))
            {
                return new GeoMatch(GeoDomainKind.Regex, regex.ToString());
            }
        }

        return null;
    }

    /// <summary>The geo rule that matched a domain: which kind of rule, and the value it matched on.</summary>
    public readonly record struct GeoMatch(GeoDomainKind Kind, string Value);

    private static string Normalize(string value)
    {
        return value.TrimEnd('.').ToLowerInvariant();
    }
}
