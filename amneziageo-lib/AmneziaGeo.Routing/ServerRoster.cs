using AmneziaGeo.Decl;

namespace AmneziaGeo.Routing;

/// <summary>
/// Which configurations the machine keeps up. The set is worked out rather than remembered: the server carrying
/// everything plus the ones rules name, less the cards switched off.
/// </summary>
public static class ServerRoster
{
    /// <summary>
    /// The configurations to raise, priority top down. With several servers off the picked one stands alone, the
    /// way it did before the mode existed; with the VPN switched off nobody stands at all.
    /// </summary>
    /// <param name="multiServer">Whether several servers work at once.</param>
    /// <param name="vpnOff">Whether everything was taken down by hand.</param>
    /// <param name="order">Configurations in priority order.</param>
    /// <param name="rules">Rules of the list the machine routes through.</param>
    /// <param name="disabled">Cards switched off.</param>
    /// <param name="picked">Configuration the user selected; read while the mode is off.</param>
    public static IReadOnlyList<string> Build(
        bool multiServer,
        bool vpnOff,
        IReadOnlyList<string> order,
        IReadOnlyList<GeoRule> rules,
        IEnumerable<string> disabled,
        string? picked)
    {
        if (vpnOff)
        {
            return [];
        }

        if (!multiServer)
        {
            return string.IsNullOrWhiteSpace(picked) ? [] : [picked.Trim()];
        }

        var off = new HashSet<string>(disabled, StringComparer.Ordinal);
        var enabled = order.Where(name => !off.Contains(name)).ToList();
        if (enabled.Count == 0)
        {
            return [];
        }

        // The head carries everything no rule sends elsewhere, so it stands whether a rule names it or not.
        var named = Named(rules);
        return [.. enabled.Where((name, index) => index == 0 || named.Contains(name))];
    }

    /// <summary>
    /// The configurations rules address by name, second choices included: a rule names a server to reach it, and a
    /// second choice is reachable only while its own tunnel stands.
    /// </summary>
    public static IReadOnlySet<string> Named(IReadOnlyList<GeoRule> rules)
    {
        var named = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in rules)
        {
            var normalized = rule.Normalized();
            if (normalized.Role != RouteRole.Proxy || normalized.ServerMode != RuleTargetMode.Server)
            {
                continue;
            }

            named.Add(normalized.Server);
            if (normalized.FallbackMode == RuleTargetMode.Server)
            {
                named.Add(normalized.Fallback);
            }
        }

        return named;
    }
}
