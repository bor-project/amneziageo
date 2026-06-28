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

    /// <summary>
    /// An application matcher (per-app tunneling). The value is a sub-typed token:
    /// "path=&lt;exe&gt;", "dir=&lt;folder&gt;", "svc=&lt;service&gt;", "pkg=&lt;sid&gt;" or "name=&lt;exe&gt;".
    /// Appended last so existing persisted enum ordinals (0..3) stay stable.
    /// </summary>
    App,
}
