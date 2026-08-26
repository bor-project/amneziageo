using AmneziaGeo.Decl;

namespace AmneziaGeo.Routing;

/// <summary>
/// The proxy rules one server carries. A server that is up and carries nothing keeps an empty set, which is what
/// takes away the ranges it held a moment ago.
/// </summary>
public sealed record ServerRules(string Server, IReadOnlyList<GeoRule> Rules);

/// <summary>
/// A rule and where it resolved to.
/// </summary>
public readonly record struct RuleVerdict(GeoRule Rule, RuleTarget Target);

/// <summary>
/// A routing list split across the servers that are up. Rules of the Direct and Block roles stay out of it: they
/// decide the same way wherever they land and are materialized with the list itself.
/// </summary>
public sealed record RoutingPlan(
    IReadOnlyList<ServerRules> Servers,
    IReadOnlyList<GeoRule> Blocked,
    IReadOnlyList<RuleVerdict> Verdicts)
{
    /// <summary>
    /// Splits the proxy rules across the fleet as it stands. A rule sent past the tunnel is carried by nobody:
    /// in split mode what no tunnel names is already direct.
    /// </summary>
    public static RoutingPlan Build(IReadOnlyList<GeoRule> rules, ServerFleet fleet)
    {
        var carried = new Dictionary<string, List<GeoRule>>(StringComparer.Ordinal);
        foreach (var server in fleet.Up)
        {
            carried[server] = [];
        }

        var blocked = new List<GeoRule>();
        var verdicts = new List<RuleVerdict>();
        foreach (var rule in rules)
        {
            var normalized = rule.Normalized();
            if (normalized.Role != RouteRole.Proxy)
            {
                continue;
            }

            var target = TargetResolver.Resolve(normalized, fleet);
            verdicts.Add(new RuleVerdict(normalized, target));
            switch (target.Kind)
            {
                case TargetKind.Auto when fleet.AnyUp:
                    carried[fleet.First].Add(normalized);
                    break;

                case TargetKind.Server when carried.TryGetValue(target.Server, out var own):
                    own.Add(normalized);
                    break;

                case TargetKind.Block:
                    blocked.Add(normalized);
                    break;
            }
        }

        return new RoutingPlan(
            [.. fleet.Up.Select(server => new ServerRules(server, carried[server]))],
            blocked,
            verdicts);
    }
}
