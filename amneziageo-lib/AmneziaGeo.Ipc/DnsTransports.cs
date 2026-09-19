namespace AmneziaGeo.Ipc;

/// <summary>
/// How the resolver behind the tunnel is asked.
/// </summary>
public static class DnsTransports
{
    /// <summary>
    /// Plain DNS first, and whatever else gets through when it is refused.
    /// </summary>
    public const string Auto = "auto";

    /// <summary>
    /// Plain DNS on port 53 and nothing else.
    /// </summary>
    public const string Plain = "plain";

    /// <summary>
    /// DNS over HTTPS and nothing else.
    /// </summary>
    public const string Doh = "doh";

    /// <summary>
    /// Transport in force where none was chosen.
    /// </summary>
    public const string Default = Auto;

    /// <summary>
    /// Every transport, in the order a picker lists them.
    /// </summary>
    public static IReadOnlyList<string> All { get; } = [Auto, Plain, Doh];

    /// <summary>
    /// Reads a transport token, answering the default for anything else.
    /// </summary>
    public static string Of(string? text)
    {
        var token = text?.Trim().ToLowerInvariant();
        return token is Auto or Plain or Doh ? token : Default;
    }

    /// <summary>
    /// Whether a token names a transport.
    /// </summary>
    public static bool IsKnown(string? text)
    {
        var token = text?.Trim().ToLowerInvariant();
        return token is Auto or Plain or Doh;
    }
}
