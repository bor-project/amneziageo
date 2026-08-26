using AmneziaGeo.Decl;

namespace AmneziaGeo.Cli;

/// <summary>
/// Reads a text payload from a file, standard input, a literal or a share link.
/// </summary>
public static class TextInput
{
    /// <summary>
    /// Where --stdin reads from; the host sets it, and it stays null where the platform has no standard input.
    /// </summary>
    public static TextReader? StandardInput { get; set; }

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

        if (StandardInput is null)
        {
            error = "--stdin is not available here; pass --file, --text or --link";
            return false;
        }

        text = StandardInput.ReadToEnd();
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
public static class Toggle
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
public static class Rules
{
    private const string _proxyRole = "proxy|";
    private static readonly string[] _roles = ["proxy", "direct", "block"];
    private static readonly string[] _kinds = ["geosite", "geoip", "domain", "cidr", "app"];
    private static readonly string[] _fields = ["server", "fallback"];

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

    /// <summary>
    /// The bucket a rule belongs to, or "proxy" where it names none.
    /// </summary>
    public static string Role(string rule)
    {
        var separator = rule.IndexOf('|');
        var head = separator > 0 ? rule[..separator].Trim().ToLowerInvariant() : string.Empty;
        return _roles.Contains(head) ? head : "proxy";
    }

    /// <summary>
    /// The rule without its role: the token and whatever tail it carries.
    /// </summary>
    public static string Bare(string rule)
    {
        var separator = rule.IndexOf('|');
        return separator > 0 && _roles.Contains(rule[..separator].Trim().ToLowerInvariant())
            ? rule[(separator + 1)..]
            : rule;
    }

    /// <summary>
    /// The token alone, without the role and without the tail: what two spellings of one rule share.
    /// </summary>
    public static string Plain(string rule) => RuleFields.Split(Bare(rule)).Token.Trim();

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

    // Catches the mistakes the agent would swallow: an unknown role, an unknown kind, an empty value, a tail
    // naming a field no rule carries.
    private static bool IsValid(string rule, bool roles)
    {
        var parts = rule.Split('|');
        var head = 0;
        if (parts.Length > 1)
        {
            if (!roles || !_roles.Contains(parts[0].ToLowerInvariant()))
            {
                return false;
            }

            head = 1;
        }

        var token = parts[head];
        var colon = token.IndexOf(':');
        if (colon <= 0 || !_kinds.Contains(token[..colon].ToLowerInvariant()) || token[(colon + 1)..].Trim().Length == 0)
        {
            return false;
        }

        return parts.Skip(head + 1).All(IsField);
    }

    // The tail a rule may carry: the server it rides and where it goes while that server is down.
    private static bool IsField(string part)
    {
        var separator = part.IndexOf('=');
        return separator > 0
            && _fields.Contains(part[..separator].Trim().ToLowerInvariant())
            && part[(separator + 1)..].Trim().Length > 0;
    }
}
