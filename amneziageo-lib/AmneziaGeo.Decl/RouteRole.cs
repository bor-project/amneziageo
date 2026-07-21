namespace AmneziaGeo.Decl;

/// <summary>
/// Which of a routing list's buckets a rule belongs to.
/// </summary>
public enum RouteRole
{
    /// <summary>
    /// Routed through the tunnel while the global proxy is off (split).
    /// </summary>
    Proxy,

    /// <summary>
    /// Kept off the tunnel in either mode, overriding a proxy match.
    /// </summary>
    Direct,

    /// <summary>
    /// Blocked outright, always, regardless of the global proxy.
    /// </summary>
    Block,
}
