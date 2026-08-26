using AmneziaGeo.Decl;

namespace AmneziaGeo.Routing;

/// <summary>
/// Resolves the server a rule rides. One table serves both modes: with several servers off every rule comes out
/// on the default route, which is the answer the machine gave before the mode existed.
/// </summary>
public static class TargetResolver
{
    /// <summary>
    /// Where the rule sends its matches against the fleet as it stands.
    /// </summary>
    public static RuleTarget Resolve(GeoRule rule, ServerFleet fleet)
    {
        if (!fleet.MultiServer)
        {
            return Auto(TargetReason.SingleServer);
        }

        var normalized = rule.Normalized();
        if (normalized.Role != RouteRole.Proxy)
        {
            return Auto(TargetReason.RoleWithoutServer);
        }

        if (normalized.ServerMode == RuleTargetMode.Best)
        {
            return BestUp(fleet, TargetReason.Best);
        }

        if (normalized.ServerMode != RuleTargetMode.Server)
        {
            return DefaultRoute(fleet, TargetReason.Auto);
        }

        if (!fleet.Knows(normalized.Server))
        {
            return Auto(TargetReason.UnknownServer);
        }

        if (fleet.IsUp(normalized.Server))
        {
            return new RuleTarget(TargetKind.Server, normalized.Server, TargetReason.Named);
        }

        return normalized.FallbackMode switch
        {
            RuleTargetMode.Best => BestUp(fleet, TargetReason.FallbackBest),
            RuleTargetMode.Server => SecondChoice(normalized.Fallback, fleet),
            RuleTargetMode.Direct => new RuleTarget(TargetKind.Direct, string.Empty, TargetReason.FallbackDirect),
            RuleTargetMode.Block => new RuleTarget(TargetKind.Block, string.Empty, TargetReason.FallbackBlocked),
            _ => DefaultRoute(fleet, TargetReason.FallbackAuto),
        };
    }

    // A second choice nothing answers to reads as an unnamed one; one that is down leaves the default route.
    private static RuleTarget SecondChoice(string fallback, ServerFleet fleet)
    {
        if (!fleet.Knows(fallback))
        {
            return Auto(TargetReason.UnknownFallback);
        }

        return fleet.IsUp(fallback)
            ? new RuleTarget(TargetKind.Server, fallback, TargetReason.FallbackServer)
            : DefaultRoute(fleet, TargetReason.FallbackDown);
    }

    // With no server up there is no tunnel to take, whichever way the rule asked for one.
    private static RuleTarget DefaultRoute(ServerFleet fleet, TargetReason reason)
    {
        return fleet.AnyUp ? Auto(reason) : Direct();
    }

    private static RuleTarget BestUp(ServerFleet fleet, TargetReason reason)
    {
        return fleet.AnyUp ? new RuleTarget(TargetKind.Server, fleet.Best, reason) : Direct();
    }

    private static RuleTarget Direct()
    {
        return new RuleTarget(TargetKind.Direct, string.Empty, TargetReason.NothingUp);
    }

    private static RuleTarget Auto(TargetReason reason)
    {
        return new RuleTarget(TargetKind.Auto, string.Empty, reason);
    }
}
