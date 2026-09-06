using System;
using System.Buffers.Binary;
using System.Text;

namespace AmneziaGeo.Android.Engine;

/// <summary>
/// Reads the destination name out of the first bytes a client sends: the server name of a TLS hello or the host
/// header of a plain request. A stream that names nothing is left to its address.
/// </summary>
internal static class StreamName
{
    private const byte Handshake = 0x16;
    private const byte ClientHello = 0x01;
    private const int ServerNameExtension = 0x0000;
    private const int HostNameEntry = 0x00;
    private const int MaxName = 253;

    /// <summary>
    /// The name the head carries, or null.
    /// </summary>
    public static string? From(ReadOnlySpan<byte> head)
    {
        if (head.Length < 5)
        {
            return null;
        }

        return head[0] == Handshake ? Tls(head) : Http(head);
    }

    // Walks a TLS client hello down to the server name extension.
    private static string? Tls(ReadOnlySpan<byte> head)
    {
        // record header, handshake header, version and random
        var at = 5;
        if (!Has(head, at, 4) || head[at] != ClientHello)
        {
            return null;
        }

        at += 4 + 2 + 32;
        if (!Skip(head, ref at, 1) || !Skip(head, ref at, 2) || !Skip(head, ref at, 1))
        {
            return null;
        }

        if (!Has(head, at, 2))
        {
            return null;
        }

        var extensionsEnd = at + 2 + BinaryPrimitives.ReadUInt16BigEndian(head[at..]);
        at += 2;
        while (at + 4 <= extensionsEnd && at + 4 <= head.Length)
        {
            var kind = BinaryPrimitives.ReadUInt16BigEndian(head[at..]);
            var size = BinaryPrimitives.ReadUInt16BigEndian(head[(at + 2)..]);
            at += 4;
            if (kind != ServerNameExtension)
            {
                at += size;
                continue;
            }

            return ServerName(head, at, Math.Min(at + size, head.Length));
        }

        return null;
    }

    // Takes the first host name of the server name list.
    private static string? ServerName(ReadOnlySpan<byte> head, int at, int end)
    {
        if (at + 5 > end)
        {
            return null;
        }

        // list length, entry kind, entry length
        at += 2;
        if (head[at] != HostNameEntry)
        {
            return null;
        }

        var size = BinaryPrimitives.ReadUInt16BigEndian(head[(at + 1)..]);
        at += 3;
        if (size == 0 || size > MaxName || at + size > end)
        {
            return null;
        }

        return Named(head.Slice(at, size));
    }

    // Takes the host header of a plain request.
    private static string? Http(ReadOnlySpan<byte> head)
    {
        if (!Method(head))
        {
            return null;
        }

        var text = Encoding.ASCII.GetString(head);
        var line = text.IndexOf("\r\n", StringComparison.Ordinal);
        while (line >= 0)
        {
            var next = text.IndexOf("\r\n", line + 2, StringComparison.Ordinal);
            var header = next < 0 ? text[(line + 2)..] : text[(line + 2)..next];
            if (header.Length == 0)
            {
                return null;
            }

            if (header.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
            {
                var value = header[5..].Trim();
                var colon = value.LastIndexOf(':');
                return Named(Encoding.ASCII.GetBytes(colon > 0 && value.IndexOf(']') < colon ? value[..colon] : value));
            }

            line = next;
        }

        return null;
    }

    // Whether the head opens with a request line of the plain protocol.
    private static bool Method(ReadOnlySpan<byte> head)
    {
        var text = Encoding.ASCII.GetString(head[..Math.Min(8, head.Length)]);
        return text.StartsWith("GET ", StringComparison.Ordinal)
            || text.StartsWith("POST ", StringComparison.Ordinal)
            || text.StartsWith("HEAD ", StringComparison.Ordinal)
            || text.StartsWith("PUT ", StringComparison.Ordinal)
            || text.StartsWith("DELETE ", StringComparison.Ordinal)
            || text.StartsWith("OPTIONS ", StringComparison.Ordinal)
            || text.StartsWith("PATCH ", StringComparison.Ordinal);
    }

    // Accepts a name of host characters only.
    private static string? Named(ReadOnlySpan<byte> raw)
    {
        foreach (var symbol in raw)
        {
            var ok = symbol is (>= (byte)'a' and <= (byte)'z') or (>= (byte)'A' and <= (byte)'Z')
                or (>= (byte)'0' and <= (byte)'9') or (byte)'.' or (byte)'-' or (byte)'_';
            if (!ok)
            {
                return null;
            }
        }

        return raw.Length > 0 ? Encoding.ASCII.GetString(raw) : null;
    }

    private static bool Has(ReadOnlySpan<byte> head, int at, int size) => at >= 0 && at + size <= head.Length;

    // Steps over a block whose length is written in the given number of bytes.
    private static bool Skip(ReadOnlySpan<byte> head, ref int at, int width)
    {
        if (!Has(head, at, width))
        {
            return false;
        }

        var size = width == 1 ? head[at] : BinaryPrimitives.ReadUInt16BigEndian(head[at..]);
        at += width + size;
        return at <= head.Length;
    }
}
