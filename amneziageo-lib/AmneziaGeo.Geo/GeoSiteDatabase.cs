using AmneziaGeo.Decl;

namespace AmneziaGeo.Geo;

/// <summary>
/// Reads v2ray geosite.dat content.
/// </summary>
public static class GeoSiteDatabase
{
    /// <summary>
    /// Returns all category codes contained in the file.
    /// </summary>
    public static IReadOnlyList<string> Categories(byte[] data)
    {
        var categories = new List<string>();
        var reader = new ProtoReader(data);
        while (!reader.End)
        {
            var (field, wireType) = reader.ReadTag();
            if (field == 1 && wireType == 2)
            {
                categories.Add(ReadCountryCode(reader.ReadBytes()));
            }
            else
            {
                reader.Skip(wireType);
            }
        }

        return categories;
    }

    /// <summary>
    /// Returns the domain rules for a category, or an empty list if absent.
    /// </summary>
    public static IReadOnlyList<GeoDomain> Domains(byte[] data, string category)
    {
        var target = category.ToUpperInvariant();
        var reader = new ProtoReader(data);
        while (!reader.End)
        {
            var (field, wireType) = reader.ReadTag();
            if (field == 1 && wireType == 2)
            {
                var entry = reader.ReadBytes();
                if (ReadCountryCode(entry) == target)
                {
                    return ReadDomains(entry);
                }
            }
            else
            {
                reader.Skip(wireType);
            }
        }

        return [];
    }

    private static string ReadCountryCode(ReadOnlySpan<byte> entry)
    {
        var reader = new ProtoReader(entry);
        while (!reader.End)
        {
            var (field, wireType) = reader.ReadTag();
            if (field == 1 && wireType == 2)
            {
                return reader.ReadString();
            }

            reader.Skip(wireType);
        }

        return string.Empty;
    }

    private static IReadOnlyList<GeoDomain> ReadDomains(ReadOnlySpan<byte> entry)
    {
        var domains = new List<GeoDomain>();
        var reader = new ProtoReader(entry);
        while (!reader.End)
        {
            var (field, wireType) = reader.ReadTag();
            if (field == 2 && wireType == 2)
            {
                domains.Add(ReadDomain(reader.ReadBytes()));
            }
            else
            {
                reader.Skip(wireType);
            }
        }

        return domains;
    }

    private static GeoDomain ReadDomain(ReadOnlySpan<byte> entry)
    {
        var kind = GeoDomainKind.Plain;
        var value = string.Empty;
        var reader = new ProtoReader(entry);
        while (!reader.End)
        {
            var (field, wireType) = reader.ReadTag();
            if (field == 1 && wireType == 0)
            {
                kind = (GeoDomainKind)reader.ReadVarint();
            }
            else if (field == 2 && wireType == 2)
            {
                value = reader.ReadString();
            }
            else
            {
                reader.Skip(wireType);
            }
        }

        return new GeoDomain(kind, value);
    }
}
