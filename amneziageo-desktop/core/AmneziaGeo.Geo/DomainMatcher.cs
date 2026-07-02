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
    /// Returns true when the domain matches any configured geo rule. The host is lower-cased once; exact
    /// and suffix rules are hash lookups, so the common cases never touch the plain/regex lists.
    /// </summary>
    public bool IsTunneled(string domain)
    {
        var host = domain.TrimEnd('.').ToLowerInvariant();
        if (host.Length == 0)
        {
            return false;
        }

        // Full: exact host. Domain: exact host, or any parent label-boundary suffix ("x.example.com"
        // matches "example.com") - the same semantics as host == value || host.EndsWith("." + value).
        if (_full.Contains(host) || _domain.Contains(host))
        {
            return true;
        }

        if (_domain.Count > 0)
        {
            for (var i = 0; i < host.Length; i++)
            {
                if (host[i] == '.' && _domain.Contains(host[(i + 1)..]))
                {
                    return true;
                }
            }
        }

        foreach (var value in _plain)
        {
            if (host.Contains(value, StringComparison.Ordinal))
            {
                return true;
            }
        }

        foreach (var regex in _regex)
        {
            if (regex.IsMatch(host))
            {
                return true;
            }
        }

        return false;
    }

    private static string Normalize(string value)
    {
        return value.TrimEnd('.').ToLowerInvariant();
    }
}
