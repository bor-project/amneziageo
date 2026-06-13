using AmneziaGeo.Decl;

namespace AmneziaGeo.Geo;

/// <summary>
/// Computes the static AllowedIPs set applied when a tunnel starts.
/// </summary>
public static class AllowedIpsResolver
{
    /// <summary>
    /// Returns the AllowedIPs to apply, given the geo settings and the config's own AllowedIPs.
    /// </summary>
    public static IReadOnlyList<string> Build(GeoSettings settings, IReadOnlyList<string> baseAllowedIps)
    {
        if (!settings.GeoSplit)
        {
            return baseAllowedIps.Count > 0 ? baseAllowedIps : ["0.0.0.0/0", "::/0"];
        }

        var result = new List<string>();
        foreach (var rule in settings.Rules)
        {
            if (rule.Kind == GeoRuleKind.Cidr)
            {
                result.Add(rule.Value);
            }
        }

        return result;
    }
}
