using System.Net;
using System.Text;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Minimal DNS wire-format reader for question names and A records.
/// </summary>
internal static class DnsMessage
{
    /// <summary>
    /// Returns the queried domain name, or null when the message is malformed.
    /// </summary>
    public static string? QuestionName(byte[] message)
    {
        if (message.Length < 13)
        {
            return null;
        }

        var offset = 12;
        return ReadName(message, ref offset);
    }

    /// <summary>
    /// Returns the QTYPE of the first question (1 = A, 28 = AAAA), or 0 when malformed.
    /// </summary>
    public static int QuestionType(byte[] message)
    {
        if (message.Length < 13)
        {
            return 0;
        }

        var offset = 12;
        SkipName(message, ref offset);
        return offset + 2 <= message.Length ? (message[offset] << 8) | message[offset + 1] : 0;
    }

    /// <summary>
    /// Builds a minimal DNS A/AAAA query (recursion desired), big-endian wire format.
    /// </summary>
    public static byte[] BuildQuery(string name, int type)
    {
        var labels = name.TrimEnd('.').Split('.', StringSplitOptions.RemoveEmptyEntries);
        var body = new List<byte>
        {
            0x12, 0x34, // id
            0x01, 0x00, // flags: RD
            0x00, 0x01, // qdcount
            0x00, 0x00, // ancount
            0x00, 0x00, // nscount
            0x00, 0x00, // arcount
        };

        foreach (var label in labels)
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            body.Add((byte)bytes.Length);
            body.AddRange(bytes);
        }

        body.Add(0);                       // root label
        body.Add((byte)(type >> 8));
        body.Add((byte)(type & 0xFF));
        body.Add(0);
        body.Add(1);                       // class IN
        return [.. body];
    }

    /// <summary>
    /// Builds an empty NOERROR/NODATA response for a query (header + question only, no answers).
    /// </summary>
    public static byte[] BuildNoData(byte[] query)
    {
        var end = 12;
        SkipName(query, ref end);
        end += 4;
        if (end < 12 || end > query.Length)
        {
            return query;
        }

        var response = new byte[end];
        Array.Copy(query, response, end);
        response[2] = (byte)(query[2] | 0x80); // QR = 1 (response), preserve opcode / RD
        response[3] = 0x80;                     // RA = 1, RCODE = 0 (NOERROR)
        response[6] = 0;
        response[7] = 0; // ANCOUNT = 0
        response[8] = 0;
        response[9] = 0; // NSCOUNT = 0
        response[10] = 0;
        response[11] = 0; // ARCOUNT = 0 (drop any EDNS OPT)
        return response;
    }

    // SERVFAIL echo of the question: lets a client fail fast and retry instead of waiting out
    // its resolver timeout when the upstream query could not be answered.
    public static byte[] BuildServFail(byte[] query)
    {
        var end = 12;
        SkipName(query, ref end);
        end += 4;
        if (end < 12 || end > query.Length)
        {
            return query;
        }

        var response = new byte[end];
        Array.Copy(query, response, end);
        response[2] = (byte)(query[2] | 0x80); // QR = 1 (response), preserve opcode / RD
        response[3] = 0x82;                     // RA = 1, RCODE = 2 (SERVFAIL)
        response[6] = 0;
        response[7] = 0; // ANCOUNT = 0
        response[8] = 0;
        response[9] = 0; // NSCOUNT = 0
        response[10] = 0;
        response[11] = 0; // ARCOUNT = 0 (drop any EDNS OPT)
        return response;
    }

    /// <summary>
    /// Returns the smallest record TTL in the answer section, or 0 when there are none.
    /// </summary>
    public static int MinTtl(byte[] message)
    {
        if (message.Length < 12)
        {
            return 0;
        }

        var questionCount = (message[4] << 8) | message[5];
        var answerCount = (message[6] << 8) | message[7];
        var offset = 12;
        for (var i = 0; i < questionCount; i++)
        {
            SkipName(message, ref offset);
            offset += 4;
        }

        var min = int.MaxValue;
        for (var i = 0; i < answerCount && offset + 10 <= message.Length; i++)
        {
            SkipName(message, ref offset);
            if (offset + 10 > message.Length)
            {
                break;
            }

            var ttl = (message[offset + 4] << 24) | (message[offset + 5] << 16) | (message[offset + 6] << 8) | message[offset + 7];
            var dataLength = (message[offset + 8] << 8) | message[offset + 9];
            offset += 10 + dataLength;
            if (ttl >= 0 && ttl < min)
            {
                min = ttl;
            }
        }

        return min == int.MaxValue ? 0 : min;
    }

    /// <summary>
    /// Returns the IPv4 (A) and IPv6 (AAAA) addresses from the answer section.
    /// </summary>
    public static IReadOnlyList<IPAddress> Addresses(byte[] message)
    {
        var result = new List<IPAddress>();
        if (message.Length < 12)
        {
            return result;
        }

        var questionCount = (message[4] << 8) | message[5];
        var answerCount = (message[6] << 8) | message[7];
        var offset = 12;
        for (var i = 0; i < questionCount; i++)
        {
            SkipName(message, ref offset);
            offset += 4;
        }

        for (var i = 0; i < answerCount && offset + 10 <= message.Length; i++)
        {
            SkipName(message, ref offset);
            if (offset + 10 > message.Length)
            {
                break; // a compressed/truncated RR name can leave < 10 bytes for the fixed record header
            }

            var type = (message[offset] << 8) | message[offset + 1];
            var dataLength = (message[offset + 8] << 8) | message[offset + 9];
            offset += 10;
            if (offset + dataLength <= message.Length && ((type == 1 && dataLength == 4) || (type == 28 && dataLength == 16)))
            {
                result.Add(new IPAddress(message[offset..(offset + dataLength)]));
            }

            offset += dataLength;
        }

        return result;
    }

    private static string ReadName(byte[] message, ref int offset)
    {
        var labels = new List<string>();
        while (offset < message.Length)
        {
            var length = message[offset];
            if (length == 0)
            {
                offset++;
                break;
            }

            if ((length & 0xC0) == 0xC0)
            {
                offset += 2;
                break;
            }

            offset++;
            if (offset + length > message.Length)
            {
                break;
            }

            labels.Add(Encoding.ASCII.GetString(message, offset, length));
            offset += length;
        }

        return string.Join('.', labels);
    }

    private static void SkipName(byte[] message, ref int offset)
    {
        while (offset < message.Length)
        {
            var length = message[offset];
            if (length == 0)
            {
                offset++;
                return;
            }

            if ((length & 0xC0) == 0xC0)
            {
                offset += 2;
                return;
            }

            offset += 1 + length;
        }
    }
}
