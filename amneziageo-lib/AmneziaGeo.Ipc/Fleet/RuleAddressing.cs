namespace AmneziaGeo.Ipc.Fleet;

/// <summary>
/// Which rules a server can be named on. A rule matched by name is matched on the answer to a lookup, and a
/// machine looks addresses up through one tunnel only, so no other server ever sees the name.
/// </summary>
public static class RuleAddressing
{
    /// <summary>
    /// What a rule matched by name is answered with when a server is named on it.
    /// </summary>
    public const string ByNameReason =
        "a rule by name rides the tunnel this machine looks addresses up through, and no other server sees the name";

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
