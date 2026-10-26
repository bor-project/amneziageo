namespace AmneziaGeo.Routing;

/// <summary>
/// Which rules of a list the route table holds, in the order of the list, and what the held and the other rules weigh.
/// </summary>
public sealed record SplitShare(IReadOnlyList<bool> Held, int HeldWeight, int LiveWeight)
{
    /// <summary>
    /// Whether a part of the list is decided on the fly.
    /// </summary>
    public bool AnyLive => LiveWeight > 0;
}
