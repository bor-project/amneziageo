using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using AmneziaGeo.Ipc.Fleet;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Answers what each tunnel is on the hook for. One tunnel on a machine holds every duty; a set of them hands
/// the duties out itself.
/// </summary>
internal class TunnelDutyRoster
{
    /// <summary>
    /// The duties of the named tunnel.
    /// </summary>
    public virtual TunnelDuties For(string name)
    {
        return TunnelDuties.Sole;
    }

    /// <summary>
    /// The tunnels a sweep must leave standing alongside the named one.
    /// </summary>
    public virtual IReadOnlyCollection<string> Standing(string name)
    {
        return [name];
    }

    /// <summary>
    /// The rules of a list the named tunnel carries. The only tunnel on a machine carries every one of them bar
    /// the ones addressed to another configuration, which go where their fallback sends them.
    /// </summary>
    public virtual async Task<IReadOnlyList<GeoRule>> ShareAsync(IStateStore store, string name, long listId, IReadOnlyList<GeoRule> rules, CancellationToken ct)
    {
        var targets = FleetTargets.Parse(await store.GetSettingAsync(FleetKeys.Targets, ct).ConfigureAwait(false));
        return Alone(name, listId, rules, targets);
    }

    /// <summary>
    /// The share of the only tunnel on a machine: a rule addressed to another configuration takes its fallback, and
    /// one whose both ends name other configurations leaves the tunnel.
    /// </summary>
    public static IReadOnlyList<GeoRule> Alone(string name, long listId, IReadOnlyList<GeoRule> rules, IReadOnlyDictionary<string, RuleRoute> targets)
    {
        if (targets.Count == 0)
        {
            return rules;
        }

        var share = new List<GeoRule>(rules.Count);
        var moved = false;
        foreach (var rule in rules)
        {
            if (rule.Role != RouteRole.Proxy
                || !targets.TryGetValue(FleetTargets.Key(listId, GeoConfigurator.Format(rule)), out var route))
            {
                share.Add(rule);
                continue;
            }

            var rides = Resolve(route.Target, name) ?? Resolve(route.Fallback, name);
            if (string.Equals(rides, name, StringComparison.Ordinal))
            {
                share.Add(rule);
                continue;
            }

            moved = true;
            if (rides == RuleTarget.Block)
            {
                share.Add(rule with { Role = RouteRole.Block });
            }
            else if (rides == RuleTarget.Direct)
            {
                share.Add(rule with { Role = RouteRole.Direct });
            }
        }

        return moved ? share : rules;
    }

    // One end of a rule on a machine running a single tunnel.
    private static string? Resolve(RuleTarget target, string name)
    {
        return target.Mode switch
        {
            RuleTarget.Block => RuleTarget.Block,
            RuleTarget.Direct => RuleTarget.Direct,
            RuleTarget.Server => string.Equals(target.Name, name, StringComparison.Ordinal) ? name : null,
            _ => name,
        };
    }
}
