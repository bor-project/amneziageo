namespace AmneziaGeo.Windows.App;

/// <summary>
/// What a routing decision does with a destination address.
/// </summary>
internal enum RouteVerdict
{
    /// <summary>
    /// In no list; follows the default route, which is the tunnel in full-tunnel mode and the physical path in split.
    /// </summary>
    None,

    /// <summary>
    /// In the proxy list; goes through the tunnel.
    /// </summary>
    Proxy,

    /// <summary>
    /// In the direct list; leaves through the physical gateway.
    /// </summary>
    Direct,

    /// <summary>
    /// In the block list; dropped before it leaves the host.
    /// </summary>
    Block,
}
