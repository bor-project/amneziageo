namespace AmneziaGeo.Routing;

/// <summary>
/// How a list that carries only what it names is held on Android: by the route table alone while it fits, by the
/// relay that decides each connection beyond that.
/// </summary>
public static class SplitBudget
{
    /// <summary>
    /// Entries the route table carries on its own.
    /// </summary>
    public const int Limit = 10_000;

    /// <summary>
    /// Addresses one name is given room to come back with at connect.
    /// </summary>
    public const int AddressesPerName = 8;

    /// <summary>
    /// What these ranges and names weigh against the limit.
    /// </summary>
    public static int Weight(int routes, int names) => routes + (names * AddressesPerName);

    /// <summary>
    /// Whether these ranges and names stay within the route table.
    /// </summary>
    public static bool Fits(int routes, int names) => Weight(routes, names) <= Limit;

    /// <summary>
    /// Whether the relay carries the list: it outgrew the route table, or every datagram has to ride the tunnel.
    /// </summary>
    public static bool Relayed(int routes, int names, bool allUdp) => allUdp || !Fits(routes, names);
}
