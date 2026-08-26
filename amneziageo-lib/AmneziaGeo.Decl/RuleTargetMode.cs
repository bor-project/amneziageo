namespace AmneziaGeo.Decl;

/// <summary>
/// Where a rule sends its traffic. One set serves both fields of a rule: the server it rides and the second
/// choice it falls to while that server is down. A server is never Direct or Block; a fallback may be either.
/// </summary>
public enum RuleTargetMode
{
    /// <summary>
    /// The first server that is up, top of the priority down; nothing up sends the traffic past the tunnel.
    /// </summary>
    Auto,

    /// <summary>
    /// The best of the servers that are up.
    /// </summary>
    Best,

    /// <summary>
    /// The configuration the rule names.
    /// </summary>
    Server,

    /// <summary>
    /// Past the tunnel, without waiting for a server to answer.
    /// </summary>
    Direct,

    /// <summary>
    /// Nowhere: the traffic is dropped until the named server answers again.
    /// </summary>
    Block,
}
