namespace AmneziaGeo.Routing;

/// <summary>
/// How a list is held on Android: by the route table while its rules fit the threshold of the settings, and by the
/// relay that decides each connection for the rules beyond it.
/// </summary>
public static class SplitBudget
{
    /// <summary>
    /// Entries the route table carries on its own until the settings name another threshold.
    /// </summary>
    public const int DefaultLimit = 11_000;

    /// <summary>
    /// Addresses one name is given room to come back with at connect.
    /// </summary>
    public const int AddressesPerName = 8;

    /// <summary>
    /// What these ranges and names weigh against the threshold.
    /// </summary>
    public static int Weight(int routes, int names) => routes + (names * AddressesPerName);

    /// <summary>
    /// Whether these ranges and names stay within the threshold of the route table.
    /// </summary>
    public static bool Fits(int routes, int names, int limit) => Weight(routes, names) <= limit;

    /// <summary>
    /// Whether one rule outweighs half the threshold, so it is decided on the fly however light the rest is.
    /// </summary>
    public static bool Heavy(int weight, int limit) => weight * 2L > limit;

    /// <summary>
    /// Splits the rules of a list in their order: a rule heavier than half the threshold is decided on the fly, the
    /// rest is held in the route table while the threshold lasts, and a rule that no longer fits beside the ones
    /// held before it is decided on the fly as well.
    /// </summary>
    public static SplitShare Share(IReadOnlyList<int> weights, int limit)
    {
        var held = new bool[weights.Count];
        var heldWeight = 0L;
        var liveWeight = 0L;
        for (var i = 0; i < weights.Count; i++)
        {
            var weight = weights[i];
            if (!Heavy(weight, limit) && heldWeight + weight <= limit)
            {
                held[i] = true;
                heldWeight += weight;
            }
            else
            {
                liveWeight += weight;
            }
        }

        return new SplitShare(held, (int)Math.Min(heldWeight, int.MaxValue), (int)Math.Min(liveWeight, int.MaxValue));
    }
}
