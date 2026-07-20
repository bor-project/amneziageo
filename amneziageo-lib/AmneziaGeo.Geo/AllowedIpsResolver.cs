namespace AmneziaGeo.Geo;

/// <summary>
/// Computes the AllowedIPs set applied when a tunnel starts.
/// </summary>
public static class AllowedIpsResolver
{
    /// <summary>
    /// Returns the AllowedIPs to apply, given the geo flag, the config's own AllowedIPs, and the materialized routes.
    /// </summary>
    public static IReadOnlyList<string> Build(bool geoSplit, IReadOnlyList<string> baseAllowedIps, IReadOnlyList<string> activeRoutes)
    {
        if (!geoSplit)
        {
            return baseAllowedIps.Count > 0 ? baseAllowedIps : ["0.0.0.0/0", "::/0"];
        }

        return activeRoutes;
    }
}
