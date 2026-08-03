namespace AmneziaGeo.Linux.Cli;

/// <summary>
/// Reads a text payload from a file, standard input, a literal or a share link.
/// </summary>
internal static class TextInput
{
    /// <summary>
    /// Reads the payload the flags point at.
    /// </summary>
    public static bool TryRead(Flags flags, out string text, out string error)
    {
        text = string.Empty;
        error = string.Empty;

        var sources = 0;
        if (flags.Value("file") is { Length: > 0 })
        {
            sources++;
        }

        if (flags.Value("text") is not null)
        {
            sources++;
        }

        if (flags.Value("link") is { Length: > 0 })
        {
            sources++;
        }

        if (flags.Has("stdin"))
        {
            sources++;
        }

        if (sources == 0)
        {
            error = "nothing to read: pass --file, --text, --link or --stdin";
            return false;
        }

        if (sources > 1)
        {
            error = "pass only one of --file, --text, --link, --stdin";
            return false;
        }

        if (flags.Value("file") is { Length: > 0 } path)
        {
            if (!File.Exists(path))
            {
                error = $"{path} does not exist";
                return false;
            }

            text = File.ReadAllText(path);
            return true;
        }

        if (flags.Value("link") is { Length: > 0 } link)
        {
            text = link;
            return true;
        }

        if (flags.Value("text") is { } literal)
        {
            text = literal;
            return true;
        }

        text = Console.In.ReadToEnd();
        if (text.Length == 0)
        {
            error = "standard input was empty";
            return false;
        }

        return true;
    }
}

/// <summary>
/// on/off arguments.
/// </summary>
internal static class Toggle
{
    /// <summary>
    /// Parses an on/off argument, accepting the usual spellings.
    /// </summary>
    public static bool TryParse(string? value, out bool on)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "on" or "true" or "1" or "yes" or "enable" or "enabled":
                on = true;
                return true;
            case "off" or "false" or "0" or "no" or "disable" or "disabled":
                on = false;
                return true;
            default:
                on = false;
                return false;
        }
    }

    /// <summary>
    /// The token the agent accepts.
    /// </summary>
    public static string Text(bool on) => on ? "on" : "off";
}

/// <summary>
/// Routing rule tokens.
/// </summary>
internal static class Rules
{
    private const string _proxyRole = "proxy|";
    private static readonly string[] _roles = ["proxy", "direct", "block"];
    private static readonly string[] _kinds = ["geosite", "geoip", "domain", "cidr", "app"];

    /// <summary>
    /// The first token a routing list would silently drop, or null when every token parses.
    /// </summary>
    public static string? FirstInvalid(IReadOnlyList<string> rules) => First(rules, roles: true);

    /// <summary>
    /// The first token a per-config geo split would silently drop, or null when every token parses.
    /// </summary>
    public static string? FirstInvalidBare(IReadOnlyList<string> rules) => First(rules, roles: false);

    /// <summary>
    /// Drops the "proxy|" prefix, the only role a per-config geo split can hold.
    /// </summary>
    public static string StripProxyRole(string rule) =>
        rule.StartsWith(_proxyRole, StringComparison.OrdinalIgnoreCase) ? rule[_proxyRole.Length..] : rule;

    private static string? First(IReadOnlyList<string> rules, bool roles)
    {
        foreach (var rule in rules)
        {
            if (!IsValid(rule, roles))
            {
                return rule;
            }
        }

        return null;
    }

    // Catches the mistakes the agent would swallow: an unknown role, an unknown kind, an empty value.
    private static bool IsValid(string rule, bool roles)
    {
        var separator = rule.IndexOf('|');
        if (separator > 0 && (!roles || !_roles.Contains(rule[..separator].ToLowerInvariant())))
        {
            return false;
        }

        var token = rule[(separator + 1)..];
        var colon = token.IndexOf(':');
        return colon > 0
            && _kinds.Contains(token[..colon].ToLowerInvariant())
            && token[(colon + 1)..].Trim().Length > 0;
    }
}
