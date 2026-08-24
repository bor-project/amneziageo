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

    /// <summary>
    /// Drops the default routes from an AllowedIPs set, leaving a tunnel that carries only the ranges it names.
    /// </summary>
    public static IReadOnlyList<string> WithoutDefaults(IReadOnlyList<string> allowedIps)
    {
        return [.. allowedIps.Where(entry => !IsDefault(entry))];
    }

    // The default route as written by a config or after the /1 split.
    private static bool IsDefault(string cidr)
    {
        return cidr.Trim() is "0.0.0.0/0" or "::/0" or "0.0.0.0/1" or "128.0.0.0/1" or "::/1" or "8000::/1";
    }
}
