namespace AmneziaGeo.Windows.App;

/// <summary>
/// What a routing decision does with a destination address.
/// </summary>
internal enum RouteVerdict
{
    /// <summary>
    /// Goes through the tunnel; the default when no rule matches.
    /// </summary>
    Proxy,

    /// <summary>
    /// Leaves through the physical gateway.
    /// </summary>
    Direct,

    /// <summary>
    /// Dropped before it leaves the host.
    /// </summary>
    Block,
}
