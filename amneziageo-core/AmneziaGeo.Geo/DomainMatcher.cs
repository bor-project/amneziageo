using System.Text.RegularExpressions;
using AmneziaGeo.Decl;

namespace AmneziaGeo.Geo;

/// <summary>
/// Decides whether a domain should be routed through the tunnel.
/// </summary>
public static class DomainMatcher
{
    /// <summary>
    /// Returns true when the domain matches any of the materialized geo domains.
    /// </summary>
    public static bool IsTunneled(string domain, IReadOnlyList<GeoDomain> domains)
    {
        var host = domain.TrimEnd('.').ToLowerInvariant();
        foreach (var entry in domains)
        {
            if (Matches(host, entry))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Matches(string host, GeoDomain entry)
    {
        var value = entry.Value.ToLowerInvariant();
        return entry.Kind switch
        {
            GeoDomainKind.Full => host == value,
            GeoDomainKind.Domain => host == value || host.EndsWith("." + value, StringComparison.Ordinal),
            GeoDomainKind.Plain => host.Contains(value, StringComparison.Ordinal),
            GeoDomainKind.Regex => Regex.IsMatch(host, entry.Value),
            _ => false,
        };
    }
}
