using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// The name of the site a connection is opened for, read from what the program sends first: the server name of a
/// TLS hello or the host of an HTTP request.
/// </summary>
internal static class SiteName
{
    /// <summary>
    /// What the bytes read so far say about the name.
    /// </summary>
    internal enum Reading
    {
        /// <summary>
        /// The name is there.
        /// </summary>
        Found,

        /// <summary>
        /// The first flight is not whole yet.
        /// </summary>
        More,

        /// <summary>
        /// The first flight names no site.
        /// </summary>
        None,
    }

    /// <summary>
    /// Most a first flight is read up to.
    /// </summary>
    public const int Limit = 16 * 1024;

    private const byte Handshake = 0x16;
    private const byte ClientHello = 0x01;
    private const int ServerNameExtension = 0;
    private const byte HostName = 0;
    private const int RecordHead = 5;

    private static readonly string[] Methods = ["GET ", "POST ", "HEAD ", "PUT ", "DELETE ", "OPTIONS ", "PATCH ", "CONNECT "];
    private static readonly byte[] HeadEnd = "\r\n\r\n"u8.ToArray();

    /// <summary>
    /// Reads the name out of the bytes a program sent first.
    /// </summary>
    public static Reading Read(ReadOnlySpan<byte> head, out string? name)
    {
        name = null;
        if (head.Length == 0)
        {
            return Reading.More;
        }

        if (head[0] == Handshake)
        {
            return FromHello(head, out name);
        }

        return FromRequest(head, out name);
    }

    // The server name of a TLS hello, once its first record is whole.
    private static Reading FromHello(ReadOnlySpan<byte> head, out string? name)
    {
        name = null;
        if (head.Length < RecordHead)
        {
            return Reading.More;
        }

        var length = BinaryPrimitives.ReadUInt16BigEndian(head[3..]);
        if (head[1] != 3 || RecordHead + length > Limit)
        {
            return Reading.None;
        }

        if (head.Length < RecordHead + length)
        {
            return Reading.More;
        }

        return ServerName(head.Slice(RecordHead, length), out name) ? Reading.Found : Reading.None;
    }

    // Walks the hello to the server name extension.
    private static bool ServerName(ReadOnlySpan<byte> record, out string? name)
    {
        name = null;
        if (record.Length < 4 || record[0] != ClientHello)
        {
            return false;
        }

        var hello = record[4..];
        // Version and random.
        var at = 2 + 32;
        if (!Skip(hello, ref at, 1) || !Skip(hello, ref at, 2) || !Skip(hello, ref at, 1) || at + 2 > hello.Length)
        {
            return false;
        }

        var end = Math.Min(hello.Length, at + 2 + BinaryPrimitives.ReadUInt16BigEndian(hello[at..]));
        at += 2;
        while (at + 4 <= end)
        {
            var type = BinaryPrimitives.ReadUInt16BigEndian(hello[at..]);
            var size = BinaryPrimitives.ReadUInt16BigEndian(hello[(at + 2)..]);
            at += 4;
            if (at + size > end)
            {
                return false;
            }

            if (type == ServerNameExtension)
            {
                return FromList(hello.Slice(at, size), out name);
            }

            at += size;
        }

        return false;
    }

    // Moves past a field that carries its own length in the given number of bytes.
    private static bool Skip(ReadOnlySpan<byte> data, ref int at, int prefix)
    {
        if (at + prefix > data.Length)
        {
            return false;
        }

        var size = prefix == 1 ? data[at] : BinaryPrimitives.ReadUInt16BigEndian(data[at..]);
        at += prefix + size;
        return at <= data.Length;
    }

    // The host name out of a server name list.
    private static bool FromList(ReadOnlySpan<byte> list, out string? name)
    {
        name = null;
        var at = 2;
        while (at + 3 <= list.Length)
        {
            var kind = list[at];
            var size = BinaryPrimitives.ReadUInt16BigEndian(list[(at + 1)..]);
            at += 3;
            if (at + size > list.Length)
            {
                return false;
            }

            if (kind == HostName)
            {
                name = Valid(Encoding.ASCII.GetString(list.Slice(at, size)));
                return name is not null;
            }

            at += size;
        }

        return false;
    }

    // The host of an HTTP request, once its head is whole.
    private static Reading FromRequest(ReadOnlySpan<byte> head, out string? name)
    {
        name = null;
        var start = Encoding.ASCII.GetString(head[..Math.Min(head.Length, 8)]);
        if (!Methods.Any(method => method.StartsWith(start, StringComparison.Ordinal) || start.StartsWith(method, StringComparison.Ordinal)))
        {
            return Reading.None;
        }

        var whole = head.IndexOf(HeadEnd);
        if (whole < 0)
        {
            return head.Length >= Limit ? Reading.None : Reading.More;
        }

        foreach (var line in Encoding.ASCII.GetString(head[..whole]).Split("\r\n"))
        {
            if (line.StartsWith("host:", StringComparison.OrdinalIgnoreCase))
            {
                name = Valid(WithoutPort(line[5..].Trim()));
                return name is null ? Reading.None : Reading.Found;
            }
        }

        return Reading.None;
    }

    // A host header without the port after it.
    private static string WithoutPort(string host)
    {
        var colon = host.LastIndexOf(':');
        return colon > 0 && host[(colon + 1)..].All(char.IsAsciiDigit) ? host[..colon] : host;
    }

    // The name in the form it is looked up in, or null for an address or anything that is no host name.
    private static string? Valid(string raw)
    {
        var name = raw.Trim().TrimEnd('.').ToLowerInvariant();
        if (name.Length == 0 || name.Length > 253 || IPAddress.TryParse(name, out _) || Uri.CheckHostName(name) != UriHostNameType.Dns)
        {
            return null;
        }

        return name;
    }
}
