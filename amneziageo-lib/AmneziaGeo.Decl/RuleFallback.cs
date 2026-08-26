namespace AmneziaGeo.Decl;

/// <summary>
/// Where a rule sends its traffic while the server it names is down.
/// </summary>
public enum RuleFallback
{
    /// <summary>
    /// Whichever server carries the default route.
    /// </summary>
    Auto,

    /// <summary>
    /// The server the rule names as its second choice.
    /// </summary>
    Server,

    /// <summary>
    /// Nowhere: the traffic is blocked until the named server answers again.
    /// </summary>
    None,
}
