namespace AmneziaGeo.Ipc.Fleet;

/// <summary>
/// Which rules are matched by name rather than by address. A machine looks addresses up through one tunnel, so
/// a rule matched by name is answered there and its name handed to the server it names.
/// </summary>
public static class RuleAddressing
{
    // The tokens matched by name rather than by address.
    private static readonly string[] Names = ["geosite:", "domain:"];

    /// <summary>
    /// Whether the rule is matched by name.
    /// </summary>
    public static bool ByName(string? token)
    {
        var text = token?.Trim() ?? string.Empty;
        foreach (var prefix in Names)
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
