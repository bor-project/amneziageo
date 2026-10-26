using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace AmneziaGeo.Routing;

/// <summary>
/// The addresses the names of a list came back with, held for the next connect of the same names: the tun is built
/// around them at once instead of waiting for the names to be asked again.
/// </summary>
public static class NameCache
{
    private const string KeyWord = "key";
    private const string ProxyWord = "proxy";
    private const string DirectWord = "direct";
    private const string BlockWord = "block";

    /// <summary>
    /// Stamp of the names of each bucket: the addresses written under it belong to these names alone.
    /// </summary>
    public static string Key(IEnumerable<string> proxy, IEnumerable<string> direct, IEnumerable<string> block)
    {
        var text = new StringBuilder();
        Append(text, ProxyWord, proxy);
        Append(text, DirectWord, direct);
        Append(text, BlockWord, block);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));

        static void Append(StringBuilder text, string word, IEnumerable<string> names)
        {
            foreach (var name in names)
            {
                text.Append(word).Append(' ').Append(name).Append('\n');
            }
        }
    }

    /// <summary>
    /// Writes the answers of the names under their stamp.
    /// </summary>
    public static string Format(string key, NameAnswers answers)
    {
        var text = new StringBuilder();
        text.Append(KeyWord).Append(' ').Append(key).Append('\n');
        Line(text, ProxyWord, answers.Proxy);
        Line(text, DirectWord, answers.Direct);
        Line(text, BlockWord, answers.Block);
        return text.ToString();

        static void Line(StringBuilder text, string word, IReadOnlyList<string> addresses)
        {
            text.Append(word);
            foreach (var address in addresses)
            {
                text.Append(' ').Append(address);
            }

            text.Append('\n');
        }
    }

    /// <summary>
    /// Reads the answers written under this stamp; null when the text holds other names or none.
    /// </summary>
    public static NameAnswers? Read(string? text, string key)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length != 4 || lines[0] != KeyWord + " " + key)
        {
            return null;
        }

        var proxy = Addresses(lines[1], ProxyWord);
        var direct = Addresses(lines[2], DirectWord);
        var block = Addresses(lines[3], BlockWord);
        return proxy is null || direct is null || block is null ? null : new NameAnswers(proxy, direct, block);
    }

    // The addresses of one bucket line, or null when the line is not the one of that bucket.
    private static IReadOnlyList<string>? Addresses(string line, string word)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts[0] != word)
        {
            return null;
        }

        return [.. parts.Skip(1).Where(Valid)];
    }

    // An address a name came back with, as the route table takes it.
    private static bool Valid(string range)
    {
        var slash = range.IndexOf('/', StringComparison.Ordinal);
        return slash > 0
            && IPAddress.TryParse(range[..slash], out var address)
            && int.TryParse(range[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var prefix)
            && prefix <= (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128);
    }
}
