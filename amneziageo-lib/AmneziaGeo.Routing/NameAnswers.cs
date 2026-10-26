namespace AmneziaGeo.Routing;

/// <summary>
/// The addresses the names of each bucket of a list came back with.
/// </summary>
public sealed record NameAnswers(IReadOnlyList<string> Proxy, IReadOnlyList<string> Direct, IReadOnlyList<string> Block)
{
    /// <summary>
    /// How many addresses the names came back with.
    /// </summary>
    public int Count => Proxy.Count + Direct.Count + Block.Count;
}
