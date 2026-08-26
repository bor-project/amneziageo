namespace AmneziaGeo.Routing;

/// <summary>
/// Where a rule's matches are sent once the machine state is known.
/// </summary>
public enum TargetKind
{
    /// <summary>
    /// Whichever server carries the default route.
    /// </summary>
    Auto,

    /// <summary>
    /// The server the verdict names.
    /// </summary>
    Server,

    /// <summary>
    /// Past the tunnel: no server is up, or the rule asked for it.
    /// </summary>
    Direct,

    /// <summary>
    /// Nowhere: dropped while the server the rule names is down.
    /// </summary>
    Block,
}

/// <summary>
/// What led a rule to its verdict; the journal and the diagnostics read it.
/// </summary>
public enum TargetReason
{
    /// <summary>
    /// Several servers are off, or the platform raises one tunnel at a time.
    /// </summary>
    SingleServer,

    /// <summary>
    /// The role rides no server of its own.
    /// </summary>
    RoleWithoutServer,

    /// <summary>
    /// The rule addresses no server and takes the default route.
    /// </summary>
    Auto,

    /// <summary>
    /// The rule asked for the best server up.
    /// </summary>
    Best,

    /// <summary>
    /// The server the rule names is up.
    /// </summary>
    Named,

    /// <summary>
    /// No server is up at all, so the matches go past the tunnel.
    /// </summary>
    NothingUp,

    /// <summary>
    /// No configuration answers to the name the rule gives.
    /// </summary>
    UnknownServer,

    /// <summary>
    /// The named server is down and the rule takes the default route.
    /// </summary>
    FallbackAuto,

    /// <summary>
    /// The named server is down and the second choice is the best server up.
    /// </summary>
    FallbackBest,

    /// <summary>
    /// The named server is down and the second choice answers.
    /// </summary>
    FallbackServer,

    /// <summary>
    /// The named server is down and the rule asked to go past the tunnel.
    /// </summary>
    FallbackDirect,

    /// <summary>
    /// The named server is down and the rule blocks rather than leak.
    /// </summary>
    FallbackBlocked,

    /// <summary>
    /// No configuration answers to the name the second choice gives.
    /// </summary>
    UnknownFallback,

    /// <summary>
    /// The named server and its second choice are both down.
    /// </summary>
    FallbackDown,
}

/// <summary>
/// What a rule resolves to: where its matches go and what led there. Equality carries the whole verdict, so a
/// caller journals a rule when its resolution changes instead of on every round.
/// </summary>
/// <param name="Kind">Where the matches go.</param>
/// <param name="Server">Configuration they ride; empty unless Kind is Server.</param>
/// <param name="Reason">What led to this.</param>
public readonly record struct RuleTarget(TargetKind Kind, string Server, TargetReason Reason)
{
    /// <summary>
    /// Whether the verdict came out of a name nothing answers to, which is what the journal warns about.
    /// </summary>
    public bool Unresolved => Reason is TargetReason.UnknownServer or TargetReason.UnknownFallback;
}
