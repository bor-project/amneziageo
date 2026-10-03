using System.Globalization;
using System.Text;

namespace AmneziaGeo.Decl;

/// <summary>
/// One journal row as a line a process hands to another: time, level, source and message, tab-separated.
/// </summary>
public static class LogLine
{
    /// <summary>
    /// Composes the line of a row.
    /// </summary>
    public static string Compose(long unixMs, string? level, string? source, string message)
    {
        return string.Join(
            '\t',
            unixMs.ToString(CultureInfo.InvariantCulture),
            level ?? string.Empty,
            Escape(source ?? string.Empty),
            Escape(message));
    }

    /// <summary>
    /// Reads a row back from its line; false when the line is not one.
    /// </summary>
    public static bool TryParse(string line, out long unixMs, out string level, out string source, out string message)
    {
        var parts = line.TrimEnd('\r').Split('\t');
        var row = parts.Length == 4;
        level = row ? parts[1] : string.Empty;
        source = row ? Unescape(parts[2]) : string.Empty;
        message = row ? Unescape(parts[3]) : string.Empty;
        return long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out unixMs) && row;
    }

    private static string Escape(string text)
    {
        return text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
    }

    private static string Unescape(string text)
    {
        if (!text.Contains('\\'))
        {
            return text;
        }

        var result = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\\' || i == text.Length - 1)
            {
                result.Append(text[i]);
                continue;
            }

            i++;
            result.Append(text[i] switch
            {
                't' => '\t',
                'r' => '\r',
                'n' => '\n',
                _ => text[i],
            });
        }

        return result.ToString();
    }
}
