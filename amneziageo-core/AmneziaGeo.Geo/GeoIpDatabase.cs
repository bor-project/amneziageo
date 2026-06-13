using System.Net;

namespace AmneziaGeo.Geo;

/// <summary>
/// Reads v2ray geoip.dat content.
/// </summary>
public static class GeoIpDatabase
{
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
