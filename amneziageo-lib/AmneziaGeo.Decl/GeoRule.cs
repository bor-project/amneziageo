namespace AmneziaGeo.Decl;

/// <summary>
/// A single split-tunnel geo rule with the list bucket it belongs to and the servers it rides.
/// </summary>
/// <param name="Kind">What the rule matches by.</param>
/// <param name="Value">What it matches.</param>
/// <param name="Role">Which bucket of the list it belongs to.</param>
/// <param name="ServerMode">Which server the match rides.</param>
/// <param name="Server">Configuration it names; read while ServerMode is Server.</param>
/// <param name="FallbackMode">Where the match goes while that server is down; read while ServerMode is Server.</param>
/// <param name="Fallback">Configuration named as the second choice; read while FallbackMode is Server.</param>
public sealed record GeoRule(
    GeoRuleKind Kind,
    string Value,
    RouteRole Role = RouteRole.Proxy,
    RuleTargetMode ServerMode = RuleTargetMode.Auto,
    string Server = "",
    RuleTargetMode FallbackMode = RuleTargetMode.Auto,
    string Fallback = "")
{
    /// <summary>
    /// The rule with the server fields cleared where nothing reads them: only a proxied match rides a server of
    /// its own, a mode carries a name only while it names one, and a server is never Direct or Block. A fallback
    /// set under an unaddressed rule is kept: the resolver ignores it, and the choice survives a change of mind.
    /// </summary>
    public GeoRule Normalized()
    {
        if (Role != RouteRole.Proxy)
        {
            return this with
            {
                ServerMode = RuleTargetMode.Auto,
                Server = string.Empty,
                FallbackMode = RuleTargetMode.Auto,
                Fallback = string.Empty,
            };
        }

        var (serverMode, server) = Settle(Named(ServerMode) ? ServerMode : RuleTargetMode.Auto, Server);
        var (fallbackMode, fallback) = Settle(FallbackMode, Fallback);
        return this with
        {
            ServerMode = serverMode,
            Server = server,
            FallbackMode = fallbackMode,
            Fallback = fallback,
        };
    }

    private static bool Named(RuleTargetMode mode) => mode is RuleTargetMode.Auto or RuleTargetMode.Best or RuleTargetMode.Server;

    private static (RuleTargetMode Mode, string Name) Settle(RuleTargetMode mode, string name)
    {
        if (mode != RuleTargetMode.Server)
        {
            return (mode, string.Empty);
        }

        var trimmed = name.Trim();
        return trimmed.Length == 0 ? (RuleTargetMode.Auto, string.Empty) : (RuleTargetMode.Server, trimmed);
    }
}
