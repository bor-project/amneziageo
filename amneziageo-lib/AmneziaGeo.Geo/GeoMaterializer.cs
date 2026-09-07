using AmneziaGeo.Decl;

namespace AmneziaGeo.Geo;

/// <summary>
/// Expands geo rules into concrete routes and domains using the merged geo index.
/// </summary>
public static class GeoMaterializer
{
    /// <summary>
    /// Materializes routes, domains, and app matchers for a set of rules; app rule values are carried verbatim.
    /// </summary>
    public static (IReadOnlyList<string> Routes, IReadOnlyList<GeoDomain> Domains, IReadOnlyList<string> Apps) Materialize(IReadOnlyList<GeoRule> rules, GeoIndex index)
    {
        var routes = new List<string>();
        var domains = new List<GeoDomain>();
        var apps = new List<string>();

        foreach (var rule in rules)
        {
            switch (rule.Kind)
            {
                case GeoRuleKind.Cidr:
                    routes.Add(rule.Value);
                    break;
                case GeoRuleKind.Domain:
                    domains.Add(new GeoDomain(GeoDomainKind.Domain, rule.Value));
                    break;
                case GeoRuleKind.GeoIp:
                case GeoRuleKind.GeoSite:
                    Expand(StripPrefix(rule.Value), index, routes, domains);
                    break;
                case GeoRuleKind.App:
                    apps.Add(rule.Value);
                    break;
            }
        }

        return (routes, domains, apps);
    }

    /// <summary>
    /// Ranges the rules name outright in the given bucket, in the order they appear.
    /// </summary>
    public static IReadOnlyList<string> NamedRanges(IReadOnlyList<GeoRule> rules, RouteRole role)
    {
        var ranges = new List<string>();
        foreach (var rule in rules)
        {
            if (rule.Kind != GeoRuleKind.Cidr || rule.Role != role || ranges.Contains(rule.Value, StringComparer.Ordinal))
            {
                continue;
            }

            ranges.Add(rule.Value);
        }

        return ranges;
    }

    /// <summary>
    /// Expands one geo key into both facets: the addresses the databases give it and the names, including the
    /// domain suffixes a country owns.
    /// </summary>
    public static void Expand(string key, GeoIndex index, List<string> routes, List<GeoDomain> domains)
    {
        routes.AddRange(index.Cidrs(key));
        domains.AddRange(index.Domains(key));
        foreach (var suffix in CountryDomains.Suffixes(key))
        {
            domains.Add(new GeoDomain(GeoDomainKind.Domain, suffix));
        }
    }

    private static string StripPrefix(string value)
    {
        var colon = value.IndexOf(':');
        return colon >= 0 ? value[(colon + 1)..] : value;
    }
}
