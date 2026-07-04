using System.Text.RegularExpressions;
using AmneziaGeo.Decl;

namespace AmneziaGeo.Geo;

/// <summary>
/// Decides whether a domain is routed through the tunnel.
/// </summary>
public sealed class DomainMatcher
{
    private readonly HashSet<string> _full = new(StringComparer.Ordinal);
    private readonly HashSet<string> _domain = new(StringComparer.Ordinal);
    private readonly List<string> _plain = [];
    private readonly List<Regex> _regex = [];

    /// <summary>
    /// ctor
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
    /// Returns true when the domain matches any geo rule.
    /// </summary>
    public bool IsTunneled(string domain) => Match(domain) is not null;

    /// <summary>
    /// Returns the geo rule that matched the domain, or null.
    /// </summary>
    public GeoMatch? Match(string domain)
    {
        var host = domain.TrimEnd('.').ToLowerInvariant();
        if (host.Length == 0)
        {
            return null;
        }

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

    /// <summary>
    /// The geo rule that matched a domain.
    /// </summary>
    public readonly record struct GeoMatch(GeoDomainKind Kind, string Value);

    private static string Normalize(string value)
    {
        return value.TrimEnd('.').ToLowerInvariant();
    }
}
