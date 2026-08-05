using System.Net;

namespace AmneziaGeo.Geo;

/// <summary>
/// Reads v2ray geoip.dat content.
/// </summary>
public static class GeoIpDatabase
{
    // An entry opens with its country code, so this much of its head decides whether the body is worth reading.
    private const int CodeProbe = 64;

    /// <summary>
    /// Returns all country codes contained in the file.
    /// </summary>
    public static IReadOnlyList<string> Countries(byte[] data)
    {
        var countries = new List<string>();
        var reader = new ProtoReader(data);
        while (!reader.End)
        {
            var (field, wireType) = reader.ReadTag();
            if (field == 1 && wireType == 2)
            {
                countries.Add(ReadCountryCode(reader.ReadBytes()));
            }
            else
            {
                reader.Skip(wireType);
            }
        }

        return countries;
    }

    /// <summary>
    /// Returns all country codes contained in the file, holding one entry at a time.
    /// </summary>
    public static IReadOnlyList<string> Countries(Stream stream)
    {
        var countries = new List<string>();
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
            countries.Add(Code(reader, length));
            reader.Seek(start + length);
        }

        return countries;
    }

    /// <summary>
    /// Returns the CIDR entries for a country, or an empty list if absent.
    /// </summary>
    public static IReadOnlyList<string> Cidrs(byte[] data, string country)
    {
        var target = country.ToUpperInvariant();
        var reader = new ProtoReader(data);
        while (!reader.End)
        {
            var (field, wireType) = reader.ReadTag();
            if (field == 1 && wireType == 2)
            {
                var entry = reader.ReadBytes();
                if (ReadCountryCode(entry) == target)
                {
                    return ReadCidrs(entry);
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
    /// Returns the CIDR entries for a country, holding one entry at a time.
    /// </summary>
    public static IReadOnlyList<string> Cidrs(Stream stream, string country)
    {
        var target = country.ToUpperInvariant();
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
                return ReadCidrs(entry.AsSpan(0, length));
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

    private static IReadOnlyList<string> ReadCidrs(ReadOnlySpan<byte> entry)
    {
        var cidrs = new List<string>();
        var reader = new ProtoReader(entry);
        while (!reader.End)
        {
            var (field, wireType) = reader.ReadTag();
            if (field == 2 && wireType == 2)
            {
                var cidr = ReadCidr(reader.ReadBytes());
                if (cidr is not null)
                {
                    cidrs.Add(cidr);
                }
            }
            else
            {
                reader.Skip(wireType);
            }
        }

        return cidrs;
    }

    private static string? ReadCidr(ReadOnlySpan<byte> entry)
    {
        byte[]? ip = null;
        var prefix = 0u;
        var reader = new ProtoReader(entry);
        while (!reader.End)
        {
            var (field, wireType) = reader.ReadTag();
            if (field == 1 && wireType == 2)
            {
                ip = reader.ReadBytes().ToArray();
            }
            else if (field == 2 && wireType == 0)
            {
                prefix = (uint)reader.ReadVarint();
            }
            else
            {
                reader.Skip(wireType);
            }
        }

        if (ip is null)
        {
            return null;
        }

        return $"{new IPAddress(ip)}/{prefix}";
    }
}
