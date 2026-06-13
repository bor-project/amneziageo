using AmneziaGeo.Decl;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Expands geo rules into concrete routes and domains using the merged geo index.
/// </summary>
internal static class GeoMaterializer
{
    /// <summary>
    /// Returns the materialized routes and domains for a set of rules.
    /// </summary>
    public static (IReadOnlyList<string> Routes, IReadOnlyList<GeoDomain> Domains) Materialize(IReadOnlyList<GeoRule> rules, GeoIndex index)
    {
        var routes = new List<string>();
        var domains = new List<GeoDomain>();

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
                    routes.AddRange(index.Cidrs(StripPrefix(rule.Value)));
                    break;
                case GeoRuleKind.GeoSite:
                    domains.AddRange(index.Domains(StripPrefix(rule.Value)));
                    break;
            }
        }

        return (routes, domains);
    }

    private static string StripPrefix(string value)
    {
        var colon = value.IndexOf(':');
        return colon >= 0 ? value[(colon + 1)..] : value;
    }
}
