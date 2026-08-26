namespace AmneziaGeo.Decl;

/// <summary>
/// The "|server=...|fallback=..." tail a rule token carries. One reading of it for the side that stores tokens
/// and the side that edits them.
/// </summary>
public static class RuleFields
{
    /// <summary>
    /// Splits the tail off a token; a configuration name carrying a bar does not survive it.
    /// </summary>
    public static (string Token, RuleTargetMode ServerMode, string Server, RuleTargetMode FallbackMode, string Fallback) Split(string text)
    {
        var parts = text.Split('|');
        var server = (Mode: RuleTargetMode.Auto, Name: string.Empty);
        var fallback = (Mode: RuleTargetMode.Auto, Name: string.Empty);
        foreach (var part in parts.Skip(1))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var value = part[(separator + 1)..].Trim();
            switch (part[..separator].Trim().ToLowerInvariant())
            {
                case "server":
                    server = Parse(value);
                    break;

                case "fallback":
                    fallback = Parse(value);
                    break;
            }
        }

        return (parts[0], server.Mode, server.Name, fallback.Mode, fallback.Name);
    }

    /// <summary>
    /// Formats both fields as the tail of a token.
    /// </summary>
    public static string Tail(RuleTargetMode serverMode, string server, RuleTargetMode fallbackMode, string fallback)
    {
        return Format("server", serverMode, server) + Format("fallback", fallbackMode, fallback);
    }

    /// <summary>
    /// Formats one field. Auto is the default and stays out of the token: a rule that addresses no server reads
    /// byte for byte as it did before the field existed.
    /// </summary>
    public static string Format(string field, RuleTargetMode mode, string name)
    {
        return mode == RuleTargetMode.Auto ? string.Empty : $"|{field}={Word(mode, name)}";
    }

    /// <summary>
    /// The word a field's value is spelled by; the reverse of <see cref="Parse"/>.
    /// </summary>
    public static string Word(RuleTargetMode mode, string name) => mode switch
    {
        RuleTargetMode.Best => "best",
        RuleTargetMode.Server => name,
        RuleTargetMode.Direct => "direct",
        RuleTargetMode.Block => "block",
        _ => "auto",
    };

    /// <summary>
    /// Reads one field. Anything but a keyword is a configuration name; "none" is how the blocking fallback used
    /// to be spelled. A configuration named after a keyword loses the round trip, as does one carrying a bar.
    /// </summary>
    public static (RuleTargetMode Mode, string Name) Parse(string value) => value.ToLowerInvariant() switch
    {
        "" or "auto" => (RuleTargetMode.Auto, string.Empty),
        "best" => (RuleTargetMode.Best, string.Empty),
        "direct" => (RuleTargetMode.Direct, string.Empty),
        "block" or "none" => (RuleTargetMode.Block, string.Empty),
        _ => (RuleTargetMode.Server, value),
    };
}
