using System.Text;

namespace AmneziaGeo.Decl;

/// <summary>
/// Reads the body and the headers an x-ui subscription answers with.
/// </summary>
public static class SubscriptionCodec
{
    /// <summary>
    /// Traffic and lifetime the panel reports in Subscription-Userinfo.
    /// </summary>
    public sealed record Usage(long Upload, long Download, long Total, DateTimeOffset? Expires);

    /// <summary>
    /// Returns the configs the subscription body carries. Links of other protocols are skipped.
    /// </summary>
    public static IReadOnlyList<VpnLinkCodec.Imported> Parse(string body)
    {
        var result = new List<VpnLinkCodec.Imported>();
        if (string.IsNullOrWhiteSpace(body))
        {
            return result;
        }

        foreach (var rawLine in Decode(body).Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (VpnLinkCodec.TryDecode(line) is { } imported)
            {
                result.Add(imported);
            }
        }

        return result;
    }

    /// <summary>
    /// Whether the text is a subscription address rather than a configuration or a link to one. Import tells the
    /// three things the panel hands out apart by what they start with, so the user picks no kind of his own.
    /// </summary>
    public static bool LooksLikeAddress(string? text)
    {
        var trimmed = text?.Trim();
        return trimmed is { Length: > 0 }
            && !trimmed.Contains('\n')
            && Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    /// <summary>
    /// Whether the address hands the subscription over in the clear.
    /// </summary>
    public static bool IsPlainAddress(string? text)
    {
        return LooksLikeAddress(text)
            && Uri.TryCreate(text?.Trim(), UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttp;
    }

    /// <summary>
    /// The name a subscription takes when the user names none: the host of its address.
    /// </summary>
    public static string AddressName(string? text)
    {
        return Uri.TryCreate(text?.Trim(), UriKind.Absolute, out var uri) ? uri.Host : string.Empty;
    }

    /// <summary>
    /// Parses the Subscription-Userinfo header, or null when it is absent.
    /// </summary>
    public static Usage? ParseUsage(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return null;
        }

        var upload = 0L;
        var download = 0L;
        var total = 0L;
        var expires = default(DateTimeOffset?);
        foreach (var part in header.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = part.IndexOf('=');
            if (equals <= 0 || !long.TryParse(part[(equals + 1)..].Trim(), out var value))
            {
                continue;
            }

            switch (part[..equals].Trim().ToLowerInvariant())
            {
                case "upload":
                    upload = value;
                    break;
                case "download":
                    download = value;
                    break;
                case "total":
                    total = value;
                    break;
                case "expire":
                    expires = value > 0 ? DateTimeOffset.FromUnixTimeSeconds(value) : null;
                    break;
            }
        }

        return new Usage(upload, download, total, expires);
    }

    /// <summary>
    /// Parses the Profile-Title header, which the panel sends either plain or as "base64:".
    /// </summary>
    public static string? ParseTitle(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return null;
        }

        var value = header.Trim();
        if (!value.StartsWith("base64:", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        var decoded = TryBase64(value["base64:".Length..].Trim());
        return decoded is null ? null : Encoding.UTF8.GetString(decoded);
    }

    /// <summary>
    /// Parses the Profile-Update-Interval header, in hours. Returns 0 when the panel names none.
    /// </summary>
    public static int ParseUpdateInterval(string? header)
    {
        if (string.IsNullOrWhiteSpace(header) || !int.TryParse(header.Trim(), out var hours) || hours <= 0)
        {
            return 0;
        }

        return hours;
    }

    // The panel base64-encodes the whole document unless subEncrypt is off.
    private static string Decode(string body)
    {
        var packed = new string(body.Where(c => !char.IsWhiteSpace(c)).ToArray());
        var bytes = TryBase64(packed);
        if (bytes is null)
        {
            return body;
        }

        var text = Encoding.UTF8.GetString(bytes);
        return text.Contains("://", StringComparison.Ordinal) || VpnLinkCodec.LooksLikeConf(text) ? text : body;
    }

    private static byte[]? TryBase64(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - (padded.Length % 4)) % 4);
        try
        {
            return Convert.FromBase64String(padded);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
