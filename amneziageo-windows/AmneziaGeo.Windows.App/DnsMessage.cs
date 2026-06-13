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
    /// Returns the IPv4 addresses from the answer section.
    /// </summary>
    public static IReadOnlyList<IPAddress> ARecords(byte[] message)
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
            var type = (message[offset] << 8) | message[offset + 1];
            var dataLength = (message[offset + 8] << 8) | message[offset + 9];
            offset += 10;
            if (type == 1 && dataLength == 4 && offset + 4 <= message.Length)
            {
                result.Add(new IPAddress(message[offset..(offset + 4)]));
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
