namespace AmneziaGeo.Decl;

/// <summary>
/// What the network of an address of the local proxy is to a neighbour, as the status snapshot names it.
/// </summary>
public static class ProxyPlaces
{
    /// <summary>
    /// A network this device serves itself: its hotspot, its tethering.
    /// </summary>
    public const string Served = "served";

    /// <summary>
    /// A Wi-Fi network the device joined.
    /// </summary>
    public const string Wifi = "wifi";

    /// <summary>
    /// A network nothing is told of.
    /// </summary>
    public const string Unnamed = "";
}
