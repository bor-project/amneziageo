using System.Text;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// The pieces of RFC 8484 this machine needs: a query carried in a URL and the type both sides name it by.
/// </summary>
internal static class LocalDohWire
{
    /// <summary>
    /// What a DNS message is called on the wire.
    /// </summary>
    public const string MessageType = "application/dns-message";

    /// <summary>
    /// The longest query served, so a stray upload cannot be read into memory.
    /// </summary>
    public const int MaxQueryBytes = 4096;

    /// <summary>
    /// The query a base64url parameter carries, null when it carries none.
    /// </summary>
    public static byte[]? FromBase64Url(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = new StringBuilder(value.Trim().Replace('-', '+').Replace('_', '/'));
        while (text.Length % 4 != 0)
        {
            text.Append('=');
        }

        try
        {
            var query = Convert.FromBase64String(text.ToString());
            return query.Length is > 0 and <= MaxQueryBytes ? query : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
