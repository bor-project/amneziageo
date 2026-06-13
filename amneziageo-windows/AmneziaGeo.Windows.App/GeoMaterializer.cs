using AmneziaGeo.Decl;
using AmneziaGeo.Geo;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Expands geo rules into concrete routes and domains using the downloaded databases.
/// </summary>
internal static class GeoMaterializer
{
    /// <summary>
    /// Returns the materialized routes and domains for a set of rules.
    /// </summary>
    public static (IReadOnlyList<string> Routes, IReadOnlyList<GeoDomain> Domains) Materialize(IReadOnlyList<GeoRule> rules)
    {
        var routes = new List<string>();
        var domains = new List<GeoDomain>();
        byte[]? geoip = null;
        byte[]? geosite = null;

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
                    geoip ??= ReadData("geoip");
                    if (geoip is not null)
                    {
                        routes.AddRange(GeoIpDatabase.Cidrs(geoip, StripPrefix(rule.Value)));
                    }

                    break;
                case GeoRuleKind.GeoSite:
                    geosite ??= ReadData("geosite");
                    if (geosite is not null)
                    {
                        domains.AddRange(GeoSiteDatabase.Domains(geosite, StripPrefix(rule.Value)));
                    }

                    break;
            }
        }

        return (routes, domains);
    }

    private static byte[]? ReadData(string kind)
    {
        var path = TunnelPaths.GeoDataFile(kind);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    private static string StripPrefix(string value)
    {
        var colon = value.IndexOf(':');
        return colon >= 0 ? value[(colon + 1)..] : value;
    }
}
