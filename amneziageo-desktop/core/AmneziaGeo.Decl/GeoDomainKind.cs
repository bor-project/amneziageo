namespace AmneziaGeo.Decl;

/// <summary>
/// Match type of a geosite domain entry.
/// </summary>
public enum GeoDomainKind
{
    /// <summary>
    /// Substring keyword match.
    /// </summary>
    Plain,
    /// <summary>
    /// Regular expression match.
    /// </summary>
    Regex,
    /// <summary>
    /// Domain suffix match.
    /// </summary>
    Domain,
    /// <summary>
    /// Exact domain match.
    /// </summary>
    Full,
}
