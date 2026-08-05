using AmneziaGeo.Decl;

namespace AmneziaGeo.Geo;

/// <summary>
/// Reads v2ray geosite.dat content.
/// </summary>
public static class GeoSiteDatabase
{
    // An entry opens with its category code, so this much of its head decides whether the body is worth reading.
    private const int CodeProbe = 64;

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
    /// Returns all category codes contained in the file, holding one entry at a time.
    /// </summary>
    public static IReadOnlyList<string> Categories(Stream stream)
    {
        var categories = new List<string>();
        var reader = new ProtoStream(stream);
        while (reader.TryReadTag(out var field, out var wireType))
        {
            if (field != 1 || wireType != 2)
            {
                reader.Skip(wireType);
                continue;
            }

            var length = reader.ReadLength();
            var start = reader.Position;
            categories.Add(Code(reader, length));
            reader.Seek(start + length);
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

    /// <summary>
    /// Returns the domain rules for a category, holding one entry at a time.
    /// </summary>
    public static IReadOnlyList<GeoDomain> Domains(Stream stream, string category)
    {
        var target = category.ToUpperInvariant();
        var reader = new ProtoStream(stream);
        while (reader.TryReadTag(out var field, out var wireType))
        {
            if (field != 1 || wireType != 2)
            {
                reader.Skip(wireType);
                continue;
            }

            var length = reader.ReadLength();
            var start = reader.Position;
            var match = Code(reader, length) == target;
            reader.Seek(start);
            if (!match)
            {
                reader.Advance(length);
                continue;
            }

            var entry = reader.Rent(length);
            try
            {
                return ReadDomains(entry.AsSpan(0, length));
            }
            finally
            {
                ProtoStream.Return(entry);
            }
        }

        return [];
    }

    // Takes an entry's code out of its head; a head too short to hold it yields nothing.
    private static string Code(ProtoStream reader, int length)
    {
        var probe = Math.Min(length, CodeProbe);
        var buffer = reader.Rent(probe);
        try
        {
            return ReadCountryCode(buffer.AsSpan(0, probe));
        }
        catch (InvalidDataException)
        {
            return string.Empty;
        }
        finally
        {
            ProtoStream.Return(buffer);
        }
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
