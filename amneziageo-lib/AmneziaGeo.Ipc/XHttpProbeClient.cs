using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using AmneziaGeo.Decl;

namespace AmneziaGeo.Ipc;

/// <summary>
/// Reads one authenticated probe over HTTP/2.
/// </summary>
internal static class XHttpProbeClient
{
    private const int MaxFrame = 16384;
    private const int MaxBody = 4096;
    private const int MaxFrames = 128;
    private static readonly byte[] Preface = "PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"u8.ToArray();
    private static readonly byte[] StatusName = EncodeHuffman(":status");
    private static readonly Dictionary<string, int> HuffmanStatuses = Enumerable.Range(100, 500)
        .ToDictionary(code => Convert.ToHexString(EncodeHuffman(code.ToString(CultureInfo.InvariantCulture))), code => code, StringComparer.Ordinal);

    internal sealed record Response(int StatusCode, byte[] Body);

    /// <summary>
    /// Uses the public authority and the explicit TLS name.
    /// </summary>
    internal static async Task<Response> SendAsync(OfferedProxy proxy, string nonce,
        Func<DnsEndPoint, CancellationToken, ValueTask<Stream>> connect, CancellationToken ct)
    {
        var connection = await connect(new DnsEndPoint(proxy.Host, proxy.Port), ct).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        using (var tls = new SslStream(connection, leaveInnerStreamOpen: true))
        {
            await tls.AuthenticateAsClientAsync(TlsOptions(proxy), ct).ConfigureAwait(false);
            if (tls.NegotiatedApplicationProtocol != SslApplicationProtocol.Http2)
            {
                throw new IOException("The proxy did not negotiate HTTP/2.");
            }

            await tls.WriteAsync(Request(proxy, nonce), ct).ConfigureAwait(false);
            await tls.FlushAsync(ct).ConfigureAwait(false);
            return await ReadResponseAsync(tls, ct).ConfigureAwait(false);
        }
    }

    internal static SslClientAuthenticationOptions TlsOptions(OfferedProxy proxy)
    {
        var options = new SslClientAuthenticationOptions
        {
            TargetHost = proxy.Tls.ServerName,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            ApplicationProtocols = [SslApplicationProtocol.Http2],
        };
        if (proxy.Tls.Pin.Length > 0)
        {
            var pin = Convert.FromHexString(proxy.Tls.Pin);
            options.RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate is not null
                && CryptographicOperations.FixedTimeEquals(SHA256.HashData(certificate.GetRawCertData()), pin);
        }

        return options;
    }

    internal static byte[] Request(OfferedProxy proxy, string nonce)
    {
        using var headers = new MemoryStream();
        headers.WriteByte(0x82);
        headers.WriteByte(0x87);
        Literal(headers, 4, "/" + proxy.Path + "/_amneziageo/probe/" + nonce);
        var authority = proxy.Host.Contains(':', StringComparison.Ordinal) ? "[" + proxy.Host + "]" : proxy.Host;
        if (proxy.Port != 443)
        {
            authority += ":" + proxy.Port.ToString(CultureInfo.InvariantCulture);
        }

        Literal(headers, 1, authority);
        Literal(headers, 23, "Bearer " + proxy.Auth.Credential, neverIndexed: true);
        Literal(headers, 24, "no-store");
        using var request = new MemoryStream();
        request.Write(Preface);
        AppendFrame(request, 4, 0, 0, [0, 1, 0, 0, 0, 0, 0, 2, 0, 0, 0, 0, 0, 6, 0, 0, 64, 0]);
        AppendFrame(request, 1, 0x5, 1, headers.ToArray());
        return request.ToArray();
    }

    private static void Literal(Stream output, int name, string value, bool neverIndexed = false)
    {
        Integer(output, name, 4, neverIndexed ? 0x10 : 0);
        var bytes = Encoding.UTF8.GetBytes(value);
        Integer(output, bytes.Length, 7, 0);
        output.Write(bytes);
    }

    private static void Integer(Stream output, int value, int bits, int prefix)
    {
        var mask = (1 << bits) - 1;
        output.WriteByte((byte)(prefix | Math.Min(value, mask)));
        if (value < mask)
        {
            return;
        }

        value -= mask;
        while (value >= 128)
        {
            output.WriteByte((byte)((value & 127) | 128));
            value >>= 7;
        }

        output.WriteByte((byte)value);
    }

    private static void AppendFrame(Stream output, byte type, byte flags, int stream, ReadOnlySpan<byte> payload)
    {
        Span<byte> header = stackalloc byte[9];
        header[0] = (byte)(payload.Length >> 16);
        header[1] = (byte)(payload.Length >> 8);
        header[2] = (byte)payload.Length;
        header[3] = type;
        header[4] = flags;
        BinaryPrimitives.WriteInt32BigEndian(header[5..], stream);
        output.Write(header);
        output.Write(payload);
    }

    private sealed record Frame(byte Type, byte Flags, int Stream, byte[] Payload);

    private static async Task<Response> ReadResponseAsync(Stream connection, CancellationToken ct)
    {
        using var body = new MemoryStream();
        using var block = new MemoryStream();
        var status = 0;
        var continuation = false;
        var ends = false;
        var dataBytes = 0;
        for (var count = 0; count < MaxFrames; count++)
        {
            var frame = await ReadFrameAsync(connection, ct).ConfigureAwait(false);
            if (count == 0 && (frame.Type != 4 || frame.Stream != 0 || (frame.Flags & 1) != 0)
                || continuation && (frame.Type != 9 || frame.Stream != 1))
            {
                throw BadResponse();
            }

            if (frame.Type == 4)
            {
                if (frame.Stream != 0 || frame.Payload.Length % 6 != 0 || (frame.Flags & 1) != 0 && frame.Payload.Length != 0)
                {
                    throw BadResponse();
                }

                if ((frame.Flags & 1) == 0)
                {
                    await ControlAsync(connection, 4, 1, [], ct).ConfigureAwait(false);
                }

                continue;
            }

            if (frame.Type == 6)
            {
                if (frame.Stream != 0 || frame.Payload.Length != 8)
                {
                    throw BadResponse();
                }

                if ((frame.Flags & 1) == 0)
                {
                    await ControlAsync(connection, 6, 1, frame.Payload, ct).ConfigureAwait(false);
                }

                continue;
            }

            if (frame.Type == 8)
            {
                if (frame.Payload.Length != 4 || (frame.Stream != 0 && frame.Stream != 1)
                    || (BinaryPrimitives.ReadUInt32BigEndian(frame.Payload) & 0x7fffffff) == 0)
                {
                    throw BadResponse();
                }

                continue;
            }

            if (frame.Type > 9)
            {
                continue;
            }

            if (frame.Type == 2)
            {
                if (frame.Stream == 0 || frame.Payload.Length != 5)
                {
                    throw BadResponse();
                }

                continue;
            }

            if (frame.Type is 3 or 5 or 7 || frame.Stream != 1)
            {
                throw BadResponse();
            }

            if (frame.Type is 1 or 9)
            {
                if (frame.Type == 9 && !continuation || frame.Type == 1 && continuation)
                {
                    throw BadResponse();
                }

                if (frame.Type == 1)
                {
                    block.SetLength(0);
                    ends = (frame.Flags & 1) != 0;
                }

                var bytes = frame.Type == 1 ? Unpad(frame.Payload, frame.Flags, priority: true) : frame.Payload.AsMemory();
                if (block.Length + bytes.Length > MaxFrame)
                {
                    throw BadResponse();
                }

                block.Write(bytes.Span);
                continuation = (frame.Flags & 4) == 0;
                if (continuation)
                {
                    continue;
                }

                var next = HeaderStatus(block.ToArray());
                if (status == 0)
                {
                    if (next is null || next < 100 || next == 101 || next < 200 && ends)
                    {
                        throw BadResponse();
                    }

                    if (next >= 200)
                    {
                        status = next.Value;
                    }
                }
                else if (next is not null || !ends)
                {
                    throw BadResponse();
                }

                if (ends)
                {
                    return new Response(status, body.ToArray());
                }

                continue;
            }

            if (frame.Type == 0)
            {
                if (status == 0)
                {
                    throw BadResponse();
                }

                dataBytes += frame.Payload.Length;
                var bytes = Unpad(frame.Payload, frame.Flags, priority: false);
                if (body.Length + bytes.Length > MaxBody || dataBytes > 65535)
                {
                    throw BadResponse();
                }

                body.Write(bytes.Span);
                if ((frame.Flags & 1) != 0)
                {
                    return new Response(status, body.ToArray());
                }
            }
        }

        throw BadResponse();
    }

    private static ReadOnlyMemory<byte> Unpad(byte[] bytes, byte flags, bool priority)
    {
        var offset = 0;
        var padding = 0;
        if ((flags & 8) != 0)
        {
            if (bytes.Length == 0)
            {
                throw BadResponse();
            }

            padding = bytes[offset++];
        }

        if (priority && (flags & 0x20) != 0)
        {
            offset += 5;
        }

        return offset + padding <= bytes.Length ? bytes.AsMemory(offset, bytes.Length - offset - padding) : throw BadResponse();
    }

    private static async Task<Frame> ReadFrameAsync(Stream input, CancellationToken ct)
    {
        var header = new byte[9];
        await input.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        var length = (header[0] << 16) | (header[1] << 8) | header[2];
        if (length > MaxFrame)
        {
            throw BadResponse();
        }

        var payload = new byte[length];
        await input.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
        return new Frame(header[3], header[4], BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(5)) & 0x7fffffff, payload);
    }

    private static async Task ControlAsync(Stream output, byte type, byte flags, byte[] payload, CancellationToken ct)
    {
        using var frame = new MemoryStream();
        AppendFrame(frame, type, flags, 0, payload);
        await output.WriteAsync(frame.ToArray(), ct).ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);
    }

    internal static int? HeaderStatus(byte[] block)
    {
        var offset = 0;
        var status = default(int?);
        var fields = 0;
        while (offset < block.Length)
        {
            var first = block[offset];
            if ((first & 0xe0) == 0x20)
            {
                if (fields > 0 || ReadInteger(block, ref offset, 5) != 0)
                {
                    throw BadResponse();
                }

                continue;
            }

            if (++fields > 128)
            {
                throw BadResponse();
            }

            var indexed = (first & 128) != 0;
            var index = ReadInteger(block, ref offset, indexed ? 7 : (first & 0x40) != 0 ? 6 : 4);
            if (index > 61 || indexed && index == 0)
            {
                throw BadResponse();
            }

            var namedStatus = index is >= 8 and <= 14;
            if (!indexed && index == 0)
            {
                var (name, huffman) = ReadString(block, ref offset);
                namedStatus = huffman ? name.Span.SequenceEqual(StatusName) : name.Span.SequenceEqual(":status"u8);
            }

            var value = indexed ? StaticStatus(index) : LiteralStatus(block, ref offset, namedStatus);
            if (namedStatus)
            {
                if (status is not null || value is null)
                {
                    throw BadResponse();
                }

                status = value;
            }
        }

        return status;
    }

    private static int? StaticStatus(int index) => index switch
    {
        8 => 200, 9 => 204, 10 => 206, 11 => 304, 12 => 400, 13 => 404, 14 => 500, _ => null,
    };

    private static int? LiteralStatus(byte[] bytes, ref int offset, bool status)
    {
        var (value, huffman) = ReadString(bytes, ref offset);
        if (!status)
        {
            return null;
        }

        if (huffman)
        {
            return HuffmanStatuses.TryGetValue(Convert.ToHexString(value.Span), out var code) ? code : null;
        }

        var raw = value.Span;
        return raw.Length == 3 && raw[0] is >= (byte)'1' and <= (byte)'5' && raw[1] is >= (byte)'0' and <= (byte)'9'
            && raw[2] is >= (byte)'0' and <= (byte)'9' ? (raw[0] - '0') * 100 + (raw[1] - '0') * 10 + raw[2] - '0' : null;
    }

    private static (ReadOnlyMemory<byte> Value, bool Huffman) ReadString(byte[] bytes, ref int offset)
    {
        if (offset >= bytes.Length)
        {
            throw BadResponse();
        }

        var huffman = (bytes[offset] & 128) != 0;
        var length = ReadInteger(bytes, ref offset, 7);
        if (length > bytes.Length - offset)
        {
            throw BadResponse();
        }

        var value = bytes.AsMemory(offset, length);
        offset += length;
        return (value, huffman);
    }

    private static int ReadInteger(byte[] bytes, ref int offset, int bits)
    {
        if (offset >= bytes.Length)
        {
            throw BadResponse();
        }

        var mask = (1 << bits) - 1;
        var value = (uint)(bytes[offset++] & mask);
        if (value < mask)
        {
            return (int)value;
        }

        for (var shift = 0; shift <= 28 && offset < bytes.Length; shift += 7)
        {
            var next = bytes[offset++];
            var extra = (ulong)(next & 127) << shift;
            if (extra + value > int.MaxValue)
            {
                throw BadResponse();
            }

            value += (uint)extra;
            if ((next & 128) == 0)
            {
                return (int)value;
            }
        }

        throw BadResponse();
    }

    internal static byte[] EncodeHuffman(string value)
    {
        using var bytes = new MemoryStream();
        var buffer = 0u;
        var bits = 0;
        foreach (var letter in value)
        {
            var (code, size) = letter switch
            {
                ':' => (0x5c, 7), 's' => (8, 5), 't' => (9, 5), 'a' => (3, 5), 'u' => (0x2d, 6),
                '0' => (0, 5), '1' => (1, 5), '2' => (2, 5),
                >= '3' and <= '9' => (0x19 + letter - '3', 6), _ => throw BadResponse(),
            };
            buffer = (buffer << size) | (uint)code;
            bits += size;
            while (bits >= 8)
            {
                bits -= 8;
                bytes.WriteByte((byte)(buffer >> bits));
                buffer &= (1u << bits) - 1;
            }
        }

        if (bits > 0)
        {
            bytes.WriteByte((byte)((buffer << (8 - bits)) | ((1u << (8 - bits)) - 1)));
        }

        return bytes.ToArray();
    }

    private static InvalidDataException BadResponse() => new("The HTTP/2 probe response is invalid.");
}
