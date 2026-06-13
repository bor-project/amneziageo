using AmneziaGeo.Decl;
using AmneziaGeo.Geo;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Merged view over multiple geo source files; later sources override earlier ones per entry.
/// </summary>
internal sealed class GeoIndex
{
    private readonly List<byte[]> _geoip;
    private readonly List<byte[]> _geosite;

    private GeoIndex(List<byte[]> geoip, List<byte[]> geosite)
    {
        _geoip = geoip;
        _geosite = geosite;
    }

    /// <summary>
    /// Loads the downloaded files for the given sources, ordered by position.
    /// </summary>
    public static GeoIndex Load(IReadOnlyList<GeoSource> sources)
    {
        var geoip = new List<byte[]>();
        var geosite = new List<byte[]>();
        foreach (var source in sources.OrderBy(s => s.Position))
        {
            var path = TunnelPaths.GeoDataFile(source.Name);
            if (!File.Exists(path))
            {
                continue;
            }

            var bytes = File.ReadAllBytes(path);
            if (source.Kind.Equals("geoip", StringComparison.OrdinalIgnoreCase))
            {
                geoip.Add(bytes);
            }
            else
            {
                geosite.Add(bytes);
            }
        }

        return new GeoIndex(geoip, geosite);
    }

    /// <summary>
    /// Returns the CIDRs for a country from the last source that defines it.
    /// </summary>
    public IReadOnlyList<string> Cidrs(string country)
    {
        IReadOnlyList<string> result = [];
        foreach (var bytes in _geoip)
        {
            var cidrs = GeoIpDatabase.Cidrs(bytes, country);
            if (cidrs.Count > 0)
            {
                result = cidrs;
            }
        }

        return result;
    }

    /// <summary>
    /// Returns the domains for a category from the last source that defines it.
    /// </summary>
    public IReadOnlyList<GeoDomain> Domains(string category)
    {
        IReadOnlyList<GeoDomain> result = [];
        foreach (var bytes in _geosite)
        {
            var domains = GeoSiteDatabase.Domains(bytes, category);
            if (domains.Count > 0)
            {
                result = domains;
            }
        }

        return result;
    }
}
