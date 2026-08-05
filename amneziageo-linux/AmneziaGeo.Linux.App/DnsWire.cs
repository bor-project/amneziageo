using System.Net;
using System.Text;

namespace AmneziaGeo.Linux.App;

/// <summary>
/// Reads the parts of a DNS message the router decides on, and builds the refusal it answers with.
/// </summary>
internal static class DnsWire
{
    private const int HeaderLength = 12;
    private const int TypeA = 1;
    private const int NoError = 0;
    private const int NameError = 3;

    /// <summary>
    /// Record type of an address record over IPv6.
    /// </summary>
    public const int TypeAaaa = 28;

    /// <summary>
    /// Reads the queried name and record type; false when the message carries no readable question.
    /// </summary>
    public static bool TryReadQuestion(byte[] buffer, int length, out string name, out int type)
    {
        name = string.Empty;
        type = 0;
        if (length < HeaderLength || ReadUInt16(buffer, 4) == 0)
        {
            return false;
        }

        var offset = HeaderLength;
        if (!TryReadName(buffer, length, ref offset, out name) || name.Length == 0 || offset + 4 > length)
        {
            return false;
        }

        type = ReadUInt16(buffer, offset);
        return true;
    }

    /// <summary>
    /// Reads the addresses the answer section carries.
    /// </summary>
    public static IReadOnlyList<IPAddress> ReadAddresses(byte[] buffer, int length)
    {
        if (length < HeaderLength)
        {
            return [];
        }

        var offset = HeaderLength;
        if (!SkipQuestions(buffer, length, ref offset))
        {
            return [];
        }

        var addresses = new List<IPAddress>();
        var answers = ReadUInt16(buffer, 6);
        for (var index = 0; index < answers; index++)
        {
            if (!TryReadName(buffer, length, ref offset, out _) || offset + 10 > length)
            {
                break;
            }

            var type = ReadUInt16(buffer, offset);
            var dataLength = ReadUInt16(buffer, offset + 8);
            offset += 10;
            if (offset + dataLength > length)
            {
                break;
            }

            if ((type == TypeA && dataLength == 4) || (type == TypeAaaa && dataLength == 16))
            {
                addresses.Add(new IPAddress(buffer.AsSpan(offset, dataLength)));
            }

            offset += dataLength;
        }

        return addresses;
    }

    /// <summary>
    /// Builds the "no such name" answer to a query.
    /// </summary>
    public static byte[]? BuildRefusal(byte[] query, int length) => BuildAnswerless(query, length, NameError);

    /// <summary>
    /// Builds the "the name is fine, it just carries no such record" answer to a query.
    /// </summary>
    public static byte[]? BuildEmpty(byte[] query, int length) => BuildAnswerless(query, length, NoError);

    // An answer that repeats the question and carries no records.
    private static byte[]? BuildAnswerless(byte[] query, int length, int code)
    {
        if (length < HeaderLength)
        {
            return null;
        }

        var offset = HeaderLength;
        if (!SkipQuestions(query, length, ref offset))
        {
            return null;
        }

        var answer = new byte[offset];
        Array.Copy(query, answer, offset);
        answer[2] = (byte)((query[2] & 0x01) | 0x80);
        answer[3] = (byte)(0x80 | code);
        WriteUInt16(answer, 6, 0);
        WriteUInt16(answer, 8, 0);
        WriteUInt16(answer, 10, 0);
        return answer;
    }

    // Walks past the question section.
    private static bool SkipQuestions(byte[] buffer, int length, ref int offset)
    {
        var questions = ReadUInt16(buffer, 4);
        for (var index = 0; index < questions; index++)
        {
            if (!TryReadName(buffer, length, ref offset, out _) || offset + 4 > length)
            {
                return false;
            }

            offset += 4;
        }

        return true;
    }

    // Reads a name, following the compression pointers a message may use.
    private static bool TryReadName(byte[] buffer, int length, ref int offset, out string name)
    {
        name = string.Empty;
        var labels = new List<string>();
        var position = offset;
        var jumped = false;
        var jumps = 0;
        while (true)
        {
            if (position >= length)
            {
                return false;
            }

            var marker = buffer[position];
            if (marker == 0)
            {
                if (!jumped)
                {
                    offset = position + 1;
                }

                break;
            }

            if ((marker & 0xC0) == 0xC0)
            {
                if (position + 1 >= length || ++jumps > 16)
                {
                    return false;
                }

                if (!jumped)
                {
                    offset = position + 2;
                    jumped = true;
                }

                position = ((marker & 0x3F) << 8) | buffer[position + 1];
                continue;
            }

            position++;
            if (position + marker > length)
            {
                return false;
            }

            labels.Add(Encoding.ASCII.GetString(buffer, position, marker));
            position += marker;
        }

        name = string.Join('.', labels);
        return true;
    }

    private static int ReadUInt16(byte[] buffer, int offset) => (buffer[offset] << 8) | buffer[offset + 1];

    private static void WriteUInt16(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 8);
        buffer[offset + 1] = (byte)value;
    }
}
