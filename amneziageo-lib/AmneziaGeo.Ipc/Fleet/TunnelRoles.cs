namespace AmneziaGeo.Ipc.Fleet;

/// <summary>
/// What a tunnel is for while several of them are up.
/// </summary>
public static class TunnelRoles
{
    /// <summary>
    /// Carries what no rule sends elsewhere and holds the resolver. One per machine.
    /// </summary>
    public const string Primary = "primary";

    /// <summary>
    /// Stands in for the primary and takes what a rule falls back to.
    /// </summary>
    public const string Reserve = "reserve";

    /// <summary>
    /// Up, but out of the balancer: it carries only what names it.
    /// </summary>
    public const string Neutral = "neutral";

    /// <summary>
    /// Role a tunnel holds until one is chosen for it.
    /// </summary>
    public const string Default = Reserve;

    /// <summary>
    /// Reads a role token, answering the default for anything else.
    /// </summary>
    public static string Of(string? text)
    {
        var token = text?.Trim().ToLowerInvariant();
        return token is Primary or Reserve or Neutral ? token : Default;
    }

    /// <summary>
    /// Whether a token names a role.
    /// </summary>
    public static bool IsKnown(string? text)
    {
        var token = text?.Trim().ToLowerInvariant();
        return token is Primary or Reserve or Neutral;
    }

    /// <summary>
    /// Whether the balancer may pick the tunnel on its own; a neutral one is only ever named.
    /// </summary>
    public static bool Balanced(string? role)
    {
        return Of(role) is Primary or Reserve;
    }
}
