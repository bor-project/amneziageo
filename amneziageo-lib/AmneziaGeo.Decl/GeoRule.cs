namespace AmneziaGeo.Decl;

/// <summary>
/// A single split-tunnel geo rule with the list bucket it belongs to and the server it rides.
/// </summary>
/// <param name="Kind">What the rule matches by.</param>
/// <param name="Value">What it matches.</param>
/// <param name="Role">Which bucket of the list it belongs to.</param>
/// <param name="Server">Configuration the match rides; empty picks whichever server carries the default route.</param>
/// <param name="FallbackMode">Where the match goes while that server is down.</param>
/// <param name="Fallback">Configuration named as the second choice; read while FallbackMode is Server.</param>
public sealed record GeoRule(
    GeoRuleKind Kind,
    string Value,
    RouteRole Role = RouteRole.Proxy,
    string Server = "",
    RuleFallback FallbackMode = RuleFallback.Auto,
    string Fallback = "")
{
    /// <summary>
    /// The rule with the server fields cleared where the role never reads them: only a proxied match rides a
    /// server of its own, so a role change leaves no name behind to resurface later.
    /// </summary>
    public GeoRule Normalized()
    {
        return Role == RouteRole.Proxy
            ? this with { Server = Server.Trim(), Fallback = FallbackMode == RuleFallback.Server ? Fallback.Trim() : string.Empty }
            : this with { Server = string.Empty, FallbackMode = RuleFallback.Auto, Fallback = string.Empty };
    }
}
