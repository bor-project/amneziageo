using AmneziaGeo.Decl;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Expands geo rules into concrete routes and domains using the merged geo index.
/// </summary>
internal static class GeoMaterializer
{
    /// <summary>
    /// Returns the materialized routes, domains, and app matchers for a set of rules. App rules are
    /// pass-through (no geo expansion): their value token ("dir=...", "path=...", "svc=...", etc.) is the
    /// matcher the per-app route watcher consumes, so it is carried verbatim alongside routes/domains.
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
                    routes.AddRange(index.Cidrs(StripPrefix(rule.Value)));
                    break;
                case GeoRuleKind.GeoSite:
                    domains.AddRange(index.Domains(StripPrefix(rule.Value)));
                    break;
                case GeoRuleKind.App:
                    apps.Add(rule.Value);
                    break;
            }
        }

        return (routes, domains, apps);
    }

    private static string StripPrefix(string value)
    {
        var colon = value.IndexOf(':');
        return colon >= 0 ? value[(colon + 1)..] : value;
    }
}
