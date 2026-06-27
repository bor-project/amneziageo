namespace AmneziaGeo.Decl;

/// <summary>
/// Kind of a split-tunnel geo rule.
/// </summary>
public enum GeoRuleKind
{
    /// <summary>
    /// v2ray geosite category (domains).
    /// </summary>
    GeoSite,

    /// <summary>
    /// v2ray geoip category (CIDRs).
    /// </summary>
    GeoIp,

    /// <summary>
    /// A single domain suffix.
    /// </summary>
    Domain,

    /// <summary>
    /// A single IP range in CIDR notation.
    /// </summary>
    Cidr,
}
