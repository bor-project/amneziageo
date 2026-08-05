using AmneziaGeo.Decl;

namespace AmneziaGeo.Geo;

/// <summary>
/// Merged view over multiple geo source files; later sources override earlier ones per entry.
/// </summary>
public sealed class GeoIndex
{
    private readonly IGeoFileStore _files;
    private readonly List<string> _geoip;
    private readonly List<string> _geosite;
    // Per-key parse memo: each Cidrs/Domains lookup re-scans the whole protobuf, and RematerializeAll queries the
    // same country/category across many lists on one shared index, so caching the result avoids the repeat scans.
    private readonly Dictionary<string, IReadOnlyList<string>> _cidrCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<GeoDomain>> _domainCache = new(StringComparer.OrdinalIgnoreCase);

    private GeoIndex(IGeoFileStore files, List<string> geoip, List<string> geosite)
    {
        _files = files;
        _geoip = geoip;
        _geosite = geosite;
    }

    /// <summary>
    /// Takes the source names for the given sources, ordered by position. The files stay on disk and are scanned
    /// per query - a geo database runs to tens of megabytes and holding one costs more than re-reading it.
    /// </summary>
    public static GeoIndex Load(IReadOnlyList<GeoSource> sources, IGeoFileStore files)
    {
        var geoip = new List<string>();
        var geosite = new List<string>();
        foreach (var source in sources.OrderBy(s => s.Position))
        {
            if (source.Kind.Equals("geoip", StringComparison.OrdinalIgnoreCase))
            {
                geoip.Add(source.Name);
            }
            else
            {
                geosite.Add(source.Name);
            }
        }

        return new GeoIndex(files, geoip, geosite);
    }

    /// <summary>
    /// Returns the CIDRs for a country from the last source that defines it.
    /// </summary>
    public IReadOnlyList<string> Cidrs(string country)
    {
        if (_cidrCache.TryGetValue(country, out var cached))
        {
            return cached;
        }

        IReadOnlyList<string> result = [];
        foreach (var name in _geoip)
        {
            var cidrs = Scan(name, stream => GeoIpDatabase.Cidrs(stream, country));
            if (cidrs.Count > 0)
            {
                result = cidrs;
            }
        }

        _cidrCache[country] = result;
        return result;
    }

    /// <summary>
    /// Returns the domains for a category from the last source that defines it.
    /// </summary>
    public IReadOnlyList<GeoDomain> Domains(string category)
    {
        if (_domainCache.TryGetValue(category, out var cached))
        {
            return cached;
        }

        IReadOnlyList<GeoDomain> result = [];
        foreach (var name in _geosite)
        {
            var domains = Scan(name, stream => GeoSiteDatabase.Domains(stream, category));
            if (domains.Count > 0)
            {
                result = domains;
            }
        }

        _domainCache[category] = result;
        return result;
    }

    /// <summary>
    /// Returns the union of geosite category codes across all sources.
    /// </summary>
    public IReadOnlyList<string> Categories()
    {
        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in _geosite)
        {
            foreach (var category in Scan(name, GeoSiteDatabase.Categories))
            {
                set.Add(category);
            }
        }

        return [.. set];
    }

    /// <summary>
    /// Returns the union of geoip country codes across all sources.
    /// </summary>
    public IReadOnlyList<string> Countries()
    {
        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in _geoip)
        {
            foreach (var country in Scan(name, GeoIpDatabase.Countries))
            {
                set.Add(country);
            }
        }

        return [.. set];
    }

    // Runs one scan over a source file. A missing file yields nothing, and a single unparseable one does not break
    // index-wide queries.
    private IReadOnlyList<T> Scan<T>(string name, Func<Stream, IReadOnlyList<T>> parse)
    {
        using var stream = _files.OpenRead(name);
        if (stream is null)
        {
            return [];
        }

        try
        {
            return parse(stream);
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or FormatException or EndOfStreamException or NotSupportedException or IOException)
        {
            return [];
        }
    }
}
