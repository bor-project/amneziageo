using AmneziaGeo.Decl;

namespace AmneziaGeo.Geo;

/// <summary>
/// Decides whether a domain should be routed through the tunnel.
/// </summary>
public static class DomainMatcher
{
    /// <summary>
    /// Returns true when the domain matches a tunneled geo rule.
    /// </summary>
    public static bool IsTunneled(string domain, GeoSettings settings)
    {
        if (!settings.GeoSplit)
        {
            return false;
        }

        var host = domain.TrimEnd('.').ToLowerInvariant();
        foreach (var rule in settings.Rules)
        {
            if (rule.Kind != GeoRuleKind.Domain)
            {
                continue;
            }

            var suffix = rule.Value.TrimStart('.').ToLowerInvariant();
            if (host == suffix || host.EndsWith("." + suffix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
